using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using System.Collections.Generic;
using Newtonsoft.Json;
using System.Net;
using System.Globalization;
using System.Linq;

public class Program
{
    private static readonly string BigCommerceApiUrl = Environment.GetEnvironmentVariable("BIGCOMMERCE_API_URL")!;
    private static readonly string BigCommerceToken = Environment.GetEnvironmentVariable("BIGCOMMERCE_TOKEN")!;
    // Batch variant endpoint: same base but /variants instead of /products
    private static readonly string BigCommerceVariantsUrl = Environment.GetEnvironmentVariable("BIGCOMMERCE_API_URL")!.Replace("/products", "/variants");
    private const string StockFilePath = "/home/adan/ftp/Stock.txt";

    // Shared client — avoids socket exhaustion and lets us set auth once
    private static readonly HttpClient _http = new HttpClient();

    static async Task Main()
    {
        _http.DefaultRequestHeaders.Add("X-Auth-Token", BigCommerceToken);
        var program = new Program();
        await program.RunUpdateAsync();
    }

    // ─── Rate-limiting helper ────────────────────────────────────────────────

    // Retries the request on HTTP 429, respecting the Retry-After header.
    private static async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> buildRequest, int maxRetries = 3)
    {
        for (int attempt = 0; attempt <= maxRetries; attempt++)
        {
            var response = await _http.SendAsync(buildRequest());

            if ((int)response.StatusCode != 429)
                return response;

            if (attempt == maxRetries)
            {
                Console.WriteLine("Rate limit exceeded after max retries.");
                return response;
            }

            int waitSeconds = 10;
            if (response.Headers.TryGetValues("Retry-After", out var values) &&
                int.TryParse(values.FirstOrDefault(), out int retryAfter))
            {
                waitSeconds = retryAfter;
            }

            Console.WriteLine($"Rate limited (429). Waiting {waitSeconds}s before retry {attempt + 1}/{maxRetries}...");
            await Task.Delay(waitSeconds * 1000);
        }

        throw new Exception("Unreachable");
    }

    private static Task<HttpResponseMessage> GetAsync(string url) =>
        SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Get, url));

    private static Task<HttpResponseMessage> PostAsync(string url, string json) =>
        SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    private static Task<HttpResponseMessage> PutAsync(string url, string json) =>
        SendWithRetryAsync(() => new HttpRequestMessage(HttpMethod.Put, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        });

    // ─── Mapping helpers (archivo en el repo, pasado via MAPPING_FILE env var) ──

    // Ruta del mapping.json: variable de entorno en CI, fallback local para desarrollo
    private static readonly string MappingFilePath =
        Environment.GetEnvironmentVariable("MAPPING_FILE")
        ?? Path.Combine(AppContext.BaseDirectory, "mapping.json");

    private Dictionary<string, int> LoadMapping()
    {
        try
        {
            if (File.Exists(MappingFilePath))
            {
                var json = File.ReadAllText(MappingFilePath, Encoding.UTF8);
                var mapping = JsonConvert.DeserializeObject<Dictionary<string, int>>(json)
                              ?? new Dictionary<string, int>();
                Console.WriteLine($"Mapping loaded: {mapping.Count} productos conocidos.");
                return mapping;
            }
            Console.WriteLine("No mapping file found. Starting fresh.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not load mapping: {ex.Message}. Starting fresh.");
        }
        return new Dictionary<string, int>();
    }

    private void SaveMapping(Dictionary<string, int> mapping)
    {
        try
        {
            File.WriteAllText(MappingFilePath, JsonConvert.SerializeObject(mapping, Formatting.Indented), Encoding.UTF8);
            Console.WriteLine($"Mapping saved: {mapping.Count} entries → {MappingFilePath}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Warning: Could not save mapping: {ex.Message}");
        }
    }

    // ─── Main flow ───────────────────────────────────────────────────────────

    public async Task RunUpdateAsync()
    {
        try
        {
            string filePath = ReadStockFilePath();
            var productGroups = ReadProductsFromTxt(filePath);
            var mapping = LoadMapping();
            int newMappings = 0;

            Console.WriteLine("Starting inventory update process...");

            // Acumular variantes por BC product ID antes de actualizar.
            // Si dos grupos del TXT (ej. TOMOÑO y TOMONO) apuntan al mismo BC product,
            // sus inventarios se suman por talla en lugar de sobreescribirse.
            var pendingUpdates = new Dictionary<int, List<ProductVariant>>();
            var pendingCreate  = new List<ProductGroup>();

            foreach (var productGroup in productGroups)
            {
                try
                {
                    int? resolvedId = null;

                    // 1. Mapping (lookup permanente por EAN+color → BC Product ID)
                    if (mapping.TryGetValue(productGroup.MappingKey, out int mappedId))
                    {
                        resolvedId = mappedId;
                    }
                    else
                    {
                        // 2. Fallback: buscar en BC por SKU y luego por nombre
                        var productDetail = await GetProductIdBySku(productGroup.Sku)
                                         ?? await GetProductIdByName(productGroup.Name);
                        if (productDetail != null)
                            resolvedId = productDetail.ProductId;
                    }

                    if (resolvedId.HasValue)
                    {
                        // Acumular variantes: si el mismo BC product ID aparece de otro grupo, sumar
                        if (!pendingUpdates.ContainsKey(resolvedId.Value))
                            pendingUpdates[resolvedId.Value] = new List<ProductVariant>();

                        foreach (var variant in productGroup.Variants)
                        {
                            var existing = pendingUpdates[resolvedId.Value]
                                .FirstOrDefault(v => v.option_values[0].label == variant.option_values[0].label);
                            if (existing != null)
                                existing.inventory_level += variant.inventory_level;
                            else
                                pendingUpdates[resolvedId.Value].Add(variant);
                        }

                        // Guardar en mapping si no estaba
                        if (!mapping.ContainsKey(productGroup.MappingKey))
                        {
                            mapping[productGroup.MappingKey] = resolvedId.Value;
                            newMappings++;
                        }
                    }
                    else
                    {
                        pendingCreate.Add(productGroup);
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error processing product '{productGroup.Name}': {ex.Message}");
                    continue;
                }
            }

            // Ejecutar actualizaciones (una por BC product ID)
            foreach (var (productId, variants) in pendingUpdates)
            {
                try
                {
                    await UpdateProductInventoryInBigCommerce(productId, variants);
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error updating product {productId}: {ex.Message}");
                }
            }

            // Crear productos que no existen en BC
            foreach (var productGroup in pendingCreate)
            {
                try
                {
                    var resolvedId = await CreateProductWithVariantsInBigCommerce(productGroup);
                    if (resolvedId.HasValue && !mapping.ContainsKey(productGroup.MappingKey))
                    {
                        mapping[productGroup.MappingKey] = resolvedId.Value;
                        newMappings++;
                    }
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"Error creating product '{productGroup.Name}': {ex.Message}");
                }
            }

            if (newMappings > 0)
                SaveMapping(mapping);

            Console.WriteLine("Inventory update process completed successfully.");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error in inventory update: {ex.Message}");
        }
    }

    public string ReadStockFilePath()
    {
        if (!File.Exists(StockFilePath))
            Console.WriteLine($"WARNING: Stock file not found at {StockFilePath}");
        else
            Console.WriteLine($"Stock file found at {StockFilePath}");
        return StockFilePath;
    }

    public List<ProductGroup> ReadProductsFromTxt(string filePath)
    {
        var groupedProducts = new Dictionary<string, ProductGroup>();

        try
        {
            foreach (var line in File.ReadAllLines(filePath, Encoding.Latin1))
            {
                var parts = line.Split('|');
                if (parts.Length < 10) continue;

                // Fix #1: include lines with inventory = 0 so we can zero them out in BC
                if (!int.TryParse(parts[8], out int inventory)) continue;
                if (inventory < 0) inventory = 0; // POS puede generar negativos, tratar como 0

                // Normalizar: strip '+' del barcode, EAN a mayúsculas
                var sku  = parts[0].TrimStart('+');
                var ean  = parts[1].Trim().ToUpper();
                var name = parts[2].Trim();

                if (string.IsNullOrWhiteSpace(ean))
                {
                    Console.WriteLine($"WARNING: EAN vacío en línea con SKU '{sku}', nombre '{name}'. Se omite.");
                    continue;
                }
                var color = parts[6].Trim();
                var size  = parts[7].Trim();

                string cleanedPrice = parts[9].Replace(",", "");
                decimal.TryParse(cleanedPrice, NumberStyles.Any, CultureInfo.InvariantCulture, out decimal price);

                // Clave normalizada: EAN en mayúsculas + color (evita duplicados por capitalización)
                string parentKey = $"{ean}_{color}";

                if (!groupedProducts.ContainsKey(parentKey))
                {
                    groupedProducts[parentKey] = new ProductGroup
                    {
                        MappingKey = parentKey,
                        Name = $"{name} {color} {ean}",
                        Sku = sku,
                        Price = price,
                        mpn = ean,
                        Variants = new List<ProductVariant>(),
                        inventory_tracking = "variant",
                        is_visible = false,
                    };
                }

                string uniqueVariantSku = $"{ean}-{color}-{size}";

                // Si la talla ya existe en el grupo, sumar inventarios (el POS puede enviar la misma talla en múltiples líneas)
                var existingVariant = groupedProducts[parentKey].Variants
                    .FirstOrDefault(v => v.option_values[0].label == size);

                if (existingVariant != null)
                {
                    existingVariant.inventory_level += inventory;
                }
                else
                {
                    groupedProducts[parentKey].Variants.Add(new ProductVariant
                    {
                        Sku = uniqueVariantSku,
                        Price = price,
                        mpn = ean,
                        inventory_level = inventory,
                        option_values = new List<ProductOption>
                        {
                            new ProductOption { label = size, option_id = 0, option_display_name = "Size" }
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Error processing a product: {ex.Message}");
        }

        Console.WriteLine($"Organized {groupedProducts.Count} products into parent-child structures.");
        return groupedProducts.Values.ToList();
    }

    public async Task<int?> CreateProductWithVariantsInBigCommerce(ProductGroup productGroup)
    {
        var productData = new
        {
            name = productGroup.Name,
            type = "physical",
            price = productGroup.Price,
            weight = 0,
            sku = productGroup.Sku,
            inventory_tracking = "variant",
            is_visible = false,
            mpn = productGroup.mpn,
            variants = productGroup.Variants.Select(variant => new
            {
                price = variant.Price,
                weight = 0.1,
                sku = variant.Sku,
                mpn = variant.mpn,
                inventory_level = variant.inventory_level,
                option_values = new List<object>
                {
                    new
                    {
                        label = variant.option_values[0].label,
                        option_display_name = "Size"
                    }
                }
            }).ToList()
        };

        var json = JsonConvert.SerializeObject(productData, Formatting.Indented);
        var response = await PostAsync(BigCommerceApiUrl, json);

        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error creating product: {productGroup.Sku} | {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            return null;
        }

        var responseContent = await response.Content.ReadAsStringAsync();
        dynamic productResponse = JsonConvert.DeserializeObject(responseContent)!;
        int productId = productResponse.data.id;
        var productSku = productResponse.data.sku;

        Console.WriteLine($"Product created: {productGroup.Name} (ID: {productId}, SKU: {productSku})");
        return productId;
    }

    // Search by parent SKU (EAN) — more reliable than name since BC names may differ
    public async Task<ProductDetail?> GetProductIdBySku(string sku)
    {
        try
        {
            string url = $"{BigCommerceApiUrl}?sku={WebUtility.UrlEncode(sku)}&limit=1";
            var response = await GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error searching product by SKU: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<dynamic>(responseContent);

            if (data?.data == null || data?.data.Count == 0)
                return null;

            return new ProductDetail { ProductId = (int)data?.data[0].id };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error getting product ID by SKU: {ex.Message}");
            return null;
        }
    }

    public async Task<ProductDetail?> GetProductIdByName(string name)
    {
        try
        {
            string url = $"{BigCommerceApiUrl}?name={WebUtility.UrlEncode(name)}&limit=1";
            var response = await GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error searching product by name: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
                return null;
            }

            var responseContent = await response.Content.ReadAsStringAsync();
            var data = JsonConvert.DeserializeObject<dynamic>(responseContent);

            if (data?.data == null || data?.data.Count == 0)
                return null;

            return new ProductDetail { ProductId = (int)data?.data[0].id };
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Unexpected error getting product ID by name: {ex.Message}");
            return null;
        }
    }

    public async Task UpdateProductInventoryInBigCommerce(int productId, List<ProductVariant> updatedVariants)
    {
        var response = await GetAsync($"{BigCommerceApiUrl}/{productId}/variants");
        if (!response.IsSuccessStatusCode)
        {
            Console.WriteLine($"Error retrieving variants: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
            return;
        }

        var content = await response.Content.ReadAsStringAsync();
        dynamic data = JsonConvert.DeserializeObject(content)!;
        var existingVariants = data.data;

        // Track which size labels from the TXT were matched to an existing BC variant
        var matchedSizeLabels = new HashSet<string>();
        var batchUpdates = new List<object>();

        // Build batch update list — match by size label, set to 0 if not in TXT
        foreach (var existingVariant in existingVariants)
        {
            int variantId = existingVariant.id;
            string sizeLabel = existingVariant.option_values?.Count > 0
                ? (string)existingVariant.option_values[0].label
                : string.Empty;

            var updatedVariant = updatedVariants.FirstOrDefault(v =>
                v.option_values?.Count > 0 &&
                v.option_values[0].label == sizeLabel);

            if (updatedVariant != null)
                matchedSizeLabels.Add(sizeLabel);

            int newInventory = updatedVariant?.inventory_level ?? 0;
            batchUpdates.Add(new { id = variantId, inventory_level = newInventory });
        }

        // Single batch PUT instead of one PUT per variant
        if (batchUpdates.Any())
        {
            var batchResponse = await PutAsync(BigCommerceVariantsUrl, JsonConvert.SerializeObject(batchUpdates));
            if (batchResponse.IsSuccessStatusCode)
                Console.WriteLine($"Batch inventory updated for product {productId}: {batchUpdates.Count} variants");
            else
                Console.WriteLine($"Error in batch update for product {productId}: {batchResponse.StatusCode} - {await batchResponse.Content.ReadAsStringAsync()}");
        }

        // Create variants from the TXT whose size label doesn't exist in BC yet
        var newVariants = updatedVariants
            .Where(v => v.option_values?.Count > 0 &&
                        !matchedSizeLabels.Contains(v.option_values[0].label) &&
                        v.inventory_level > 0)
            .ToList();

        if (newVariants.Any())
        {
            // Step 1: Get the Size option_id for this product
            var optionsResponse = await GetAsync($"{BigCommerceApiUrl}/{productId}/options");
            if (!optionsResponse.IsSuccessStatusCode)
            {
                Console.WriteLine($"Error fetching options for product {productId}: {optionsResponse.StatusCode}");
                return;
            }

            dynamic optionsData = JsonConvert.DeserializeObject(await optionsResponse.Content.ReadAsStringAsync())!;
            int? sizeOptionId = null;
            foreach (var opt in optionsData.data)
            {
                if (((string)opt.display_name).Equals("Size", StringComparison.OrdinalIgnoreCase))
                {
                    sizeOptionId = (int)opt.id;
                    break;
                }
            }

            if (!sizeOptionId.HasValue)
            {
                Console.WriteLine($"No Size option found for product {productId}, skipping new variants.");
                return;
            }

            // Step 2: Get existing option values to avoid duplicate creation
            var valuesResponse = await GetAsync($"{BigCommerceApiUrl}/{productId}/options/{sizeOptionId}/values");
            var existingValueIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            if (valuesResponse.IsSuccessStatusCode)
            {
                dynamic valuesData = JsonConvert.DeserializeObject(await valuesResponse.Content.ReadAsStringAsync())!;
                foreach (var val in valuesData.data)
                    existingValueIds[(string)val.label] = (int)val.id;
            }

            foreach (var newVariant in newVariants)
            {
                string sizeLabel = newVariant.option_values[0].label;

                // Step 3: Get or create the option value to obtain value_id
                int valueId;
                if (existingValueIds.TryGetValue(sizeLabel, out int existingId))
                {
                    valueId = existingId;
                }
                else
                {
                    var createValueResponse = await PostAsync(
                        $"{BigCommerceApiUrl}/{productId}/options/{sizeOptionId}/values",
                        JsonConvert.SerializeObject(new { label = sizeLabel, sort_order = GetSizeSortOrder(sizeLabel) }));

                    if (!createValueResponse.IsSuccessStatusCode)
                    {
                        Console.WriteLine($"Error creating option value '{sizeLabel}' for product {productId}: {createValueResponse.StatusCode} - {await createValueResponse.Content.ReadAsStringAsync()}");
                        continue;
                    }

                    dynamic createValueData = JsonConvert.DeserializeObject(await createValueResponse.Content.ReadAsStringAsync())!;
                    valueId = (int)createValueData.data.id;
                    existingValueIds[sizeLabel] = valueId;
                }

                // Step 4: Create the variant with the correct option_values payload
                var variantData = new
                {
                    sku = newVariant.Sku,
                    price = newVariant.Price,
                    weight = 0.1,
                    mpn = newVariant.mpn,
                    inventory_level = newVariant.inventory_level,
                    option_values = new List<object>
                    {
                        new { id = valueId, option_id = sizeOptionId.Value }
                    }
                };

                var createResponse = await PostAsync(
                    $"{BigCommerceApiUrl}/{productId}/variants",
                    JsonConvert.SerializeObject(variantData));

                if (createResponse.IsSuccessStatusCode)
                    Console.WriteLine($"New variant created for product {productId}: SKU {newVariant.Sku}");
                else
                    Console.WriteLine($"Error creating variant SKU {newVariant.Sku}: {createResponse.StatusCode} - {await createResponse.Content.ReadAsStringAsync()}");
            }
        }
    }

    // Converts a size label to a numeric sort_order so variants appear in sequence.
    // Numeric sizes (22, 22.5, 23…) → value × 10  (22→220, 22.5→225)
    // Clothing sizes (XS/S/M/L/XL…) → fixed positions
    private static int GetSizeSortOrder(string label)
    {
        if (float.TryParse(label,
            System.Globalization.NumberStyles.Any,
            System.Globalization.CultureInfo.InvariantCulture,
            out float num))
            return (int)(num * 10);

        return label.Trim().ToUpper() switch
        {
            "XS"   => 10,
            "S"    => 20,
            "M"    => 30,
            "L"    => 40,
            "XL"   => 50,
            "XXL"  => 60,
            "XXXL" => 70,
            _      => 999
        };
    }

    //Helpers

    //Generates a random SKU by appending a unique number
    private string GenerateRandomSku()
    {
        Random random = new Random();
        return random.Next(1000, 9999).ToString();
    }

    public async Task CreateVariantsInBigCommerce(int productId, List<Product> variants)
    {
        foreach (var variant in variants)
        {
            var variantData = new
            {
                product_id = productId,
                sku = variant.Sku,
                price = variant.Price,
                weight = 0.1,
                inventory_level = variant.Inventory,
                option_values = new[]
                {
                    new
                    {
                        label = variant.Size,
                        option_display_name = "Size"
                    }
                }
            };

            var json = JsonConvert.SerializeObject(variantData);
            var response = await PostAsync($"{BigCommerceApiUrl}/{productId}/variants", json);

            if (response.IsSuccessStatusCode)
                Console.WriteLine($"Variant {variant.Size} created for {productId}");
            else
                Console.WriteLine($"Error creating variant: {response.StatusCode} - {await response.Content.ReadAsStringAsync()}");
        }
    }

    private string ModifySku(string originalSku)
    {
        if (string.IsNullOrEmpty(originalSku)) return originalSku;

        char lastChar = originalSku[^1];
        char newChar;

        do
        {
            newChar = (char)('0' + new Random().Next(0, 10));
        } while (newChar == lastChar);

        return originalSku.Substring(0, originalSku.Length - 1) + newChar;
    }


    public class Product
    {
        public string Sku { get; set; } = null!;
        public string Ean { get; set; } = null!;
        public string Name { get; set; } = null!;
        public string Color { get; set; } = null!;
        public string Size { get; set; } = null!;
        public int Inventory { get; set; }
        public decimal Price { get; set; }
        public string upc { get; set; } = null!;
        public string Type { get; set; } = null!;
    }

    public class ProductDetail
    {
        public int ProductId { get; set; }
    }

    public class ProductGroup
    {
        public string MappingKey { get; set; } = null!; // "{EAN}_{color}" — clave permanente de asociación
        public string Name { get; set; } = null!;
        public string Sku { get; set; } = null!;
        public decimal Price { get; set; }
        public string mpn { get; set; } = null!;
        public List<ProductVariant> Variants { get; set; } = null!;
        public string inventory_tracking { get; set; } = null!;
        public Boolean is_visible { get; set; }
    }

    public class ProductVariant
    {
        public string Sku { get; set; } = null!;
        public decimal Price { get; set; }
        public string mpn { get; set; } = null!;

        public int inventory_level { get; set; }
        public List<ProductOption> option_values { get; set; } = null!;
    }

    public class ProductOption
    {
        public string label { get; set; } = null!;
        public int option_id { get; set; }
        public string option_display_name { get; set; } = null!;
    }
}
