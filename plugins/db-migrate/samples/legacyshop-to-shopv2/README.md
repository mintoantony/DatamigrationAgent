# LegacyShop → ShopV2 sample pair

A small, deterministic source/target pair used by the db-migrate integration tests and by `dbm demo`.

| File | Run against | Purpose |
|---|---|---|
| `legacyshop.sql` | empty source database | LegacyShop schema (`dbo`): 8 tables, one heap (`AUDIT_LOG`), one empty table (`TMP_IMPORT`) |
| `seed.sql` | LegacyShop, after `legacyshop.sql` | Data for scale `$(scale)`, then `FK_ORD_CUST` added `WITH NOCHECK` |
| `shopv2.sql` | empty target database | ShopV2 schema (`app`): renamed/split columns, FK cycle Customers ↔ Addresses, computed column, rowversion, trigger |

Row counts at scale `s`: CUST 1000·s, ADDR 1500·s, PROD 200, ORD_STATUS 4, ORD_HDR 3000·s + 5, ORD_LINE 9000·s + 2,
AUDIT_LOG 5000·s, TMP_IMPORT 0.

Deliberate anomalies (same at every scale):

- 2 orphan orders (`CUST_ID` −1 and −2) with one line each — rejected by the target FK;
- 3 orders with a 300-character `CMNT` (target `Comment` is `nvarchar(200)`) and no lines — rejected as truncation;
- 1 order line with `QTY = 0` — rejected by `CK_OrderLines_Quantity`;
- `FK_ORD_CUST` is untrusted, `EMAIL_ADDR` is 10 % NULL, `FAX_NO` 98 % NULL, `NOTES` uses the deprecated `text` type.

With the approved ground-truth mapping and "skip and log", a transfer rejects exactly 8 rows.

Manual setup with sqlcmd (integrated auth, LocalDB shown):

    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -Q "CREATE DATABASE LegacyShop; CREATE DATABASE ShopV2;"
    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d LegacyShop -b -i legacyshop.sql
    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d LegacyShop -b -v scale=1 -i seed.sql
    sqlcmd -S "(localdb)\MSSQLLocalDB" -E -d ShopV2 -b -i shopv2.sql

The scripts are embedded in `Dbm.dll` (`Dbm.Core.Samples.SampleSql`), so `dbm demo` needs no files on disk.
