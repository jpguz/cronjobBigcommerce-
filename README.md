# cronjobBigcommerce- — Adrian Inventory Sync

Servicio personalizado que lee datos de inventario txt vía FTP y mantiene stock de productos actualizado en BigCommerce vía API.

## Descripción rápida

Adrian es una tienda retail de ropa. Este cron sincroniza su POS con BigCommerce diariamente:

1. **Entrada:** Stock.txt desde FTP krypton (sube el POS ~10pm CST)
2. **Procesamiento:** Parser .NET 8 que agrupa por EAN+color, crea/actualiza variantes
3. **Salida:** Productos y variantes en BigCommerce via API

## Stack
- **.NET 8** — binario self-contained linux-x64
- **Single file:** `Program.cs`
- **Deps:** Newtonsoft.Json 13.0.3
- **FTP:** vsftpd en krypton (local filesystem, sin FluentFTP)

## Infraestructura

| Componente | Ubicación | Detalles |
|---|---|---|
| **Repo fork** | jpguz/cronjobBigcommerce- | Activo, JP es maintainer |
| **VPS runner** | krypton (Hetzner) | Binario: `~/cronjob-bc/cronJobMx` |
| **FTP server** | krypton:46.225.27.154:21 | vsftpd, usuario `adan`, chroot `/home/adan` |
| **Stock.txt** | /home/adan/ftp/Stock.txt | Actualizado por POS ~10pm CST |
| **mapping.json** | /home/jp/cronjob-bc/repo/ | ~14,435+ registros EAN_color → BC product ID |
| **Logs** | /home/jp/cronjob-bc/logs/ | run-YYYYMMDD-HHMMSS.log |
| **Crontab** | VPS krypton | 8 UTC (2am CST) + retry 10 UTC (4am CST) |

## BigCommerce API

- **Store:** oqdevwmnzx (adrian.com.mx)
- **Endpoint:** https://api.bigcommerce.com/stores/oqdevwmnzx/v3/catalog/products
- **Token:** hb872xeh3lqy56zdp4xwmf0qq07li41 (env: BIGCOMMERCE_TOKEN)
- **Catálogo actual:** 14,435+ productos, 103 huérfanos limpios (2026-03-07)

## Reglas de negocio

| Concepto | Implementación |
|---|---|
| **Agrupación** | Clave padre = `{EAN}_{color}` (EAN mayúsculas) |
| **Variante** | Solo talla — colores son productos separados en BC |
| **Visibilidad** | `is_visible = false` al crear (cliente activa manualmente) |
| **Sin stock** | Productos se crean con inventory=0, no se saltan |
| **Limpieza** | Variantes en BC sin stock en TXT → inventory=0 |
| **Naming** | BC: `"{nombre} {color} {EAN}"` |
| **SKU variante** | `"{EAN}-{color}-{talla}"` |
| **Género (tallas)** | 22-24=mujeres, 26-30=hombres |

## Stock.txt (entrada)

```
barcode|EAN|nombre|brand|categoria|genero|color|talla|inventario|precio
```

- **Encoding:** Latin1
- **Delimitador:** pipe `|`
- **Campos:** 10 (barcode→precio)
- **Actualización:** POS sube ~10pm CST via FTP

## Flujo de sincronización

1. VPS ejecuta `/home/jp/cronjob-bc/run.sh` (2am CST)
2. Script chequea flag `/tmp/bc-sync-ok-YYYYMMDD` → evita duplicados
3. Binario lee Stock.txt desde `/home/adan/ftp/Stock.txt`
4. Parsea, agrupa por `{EAN}_{color}`, acumula variantes
5. Lookup en mapping.json (rápido) → SKU search → name search → crear
6. Batch PUT `/variants` por producto (1 request/producto)
7. Crear productos nuevos al final (POST `/`)
8. Commit mapping.json actualizado
9. Log en `~/cronjob-bc/logs/`

## Deploy de nueva versión

```bash
# En tu máquina local
dotnet publish -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -o ./publish-linux

# Copia al VPS
scp ./publish-linux/cronJobMx jp@clawbot:~/cronjob-bc/cronJobMx

# El siguiente cron ejecutará la nueva versión
```

## Scripts de utilidad

| Script | Función | Uso |
|---|---|---|
| `compare.py` | Cruza TXT vs BC | Genera CSV comparativa + lista de SKUs faltantes |
| `cleanup_orphans.py` | Limpia huérfanos | `--dry-run`, `--hide`, `--delete` |
| `fix_sort_order.py` | Reordena tallas | Normaliza option values por numérico |

## Bugs históricos (resueltos)

- ✅ EAN duplicate creaba sobreescrituras → fix: acumular por BC product ID
- ✅ Variantes 422 (faltaba option_id) → fix: GET/POST option values
- ✅ Tallas desordenadas → fix: `GetSizeSortOrder()`
- ✅ SKU con prefijo `+` no encontraba → fix: TrimStart
- ✅ EAN case-sensitivity → fix: normalizar mayúsculas
- ✅ Inventario negativo → fix: clamp a 0
- ✅ mapping.json BOM (Windows) → fix: `encoding='utf-8-sig'`
- ✅ Git push 403 en GH Actions → fix: `permissions: contents: write`
- ✅ Ghost SKUs 409 → nota: BC tarda 24h en liberar

## Errores conocidos

- `AN92181914`, `IR99623028`: label vacío en variante del TXT → 422 al POST. **Causa:** POS del cliente. **Acción:** coordinar con Adrian.

## Estado (2026-03-09)

✅ FTP migrado a krypton  
✅ Sync diario funcionando 2 veces al día  
✅ 14,435+ productos en catálogo  
✅ mapping.json commitea después de cada run  
⏳ Front-end changes (pendiente spec del cliente)

---

**Contacto cliente:** Adrian (adrian.com.mx)  
**Maintainer:** JP (@jpguz)  
**Memory:** [Adrian-BigCommerce-CronJob.md en AI-Memories](https://github.com/jpguz/AI-Memories)
