-- LegacyShop seed data. Deterministic and set-based; $(scale) is replaced by SampleSql.Seed(scale)
-- (or by sqlcmd -v scale=N). Row counts: CUST 1000*scale, ADDR 1500*scale, PROD 200, ORD_STATUS 4,
-- ORD_HDR 3000*scale + 5, ORD_LINE 9000*scale + 2, AUDIT_LOG 5000*scale, TMP_IMPORT 0.
-- Fixed anomalies (independent of scale):
--   * 2 orphan orders (CUST_ID -1 and -2, no such customer), 1 line each;
--   * 3 orders whose CMNT is exactly 300 characters (valid customers, no lines);
--   * 1 order line with QTY = 0 (ORD_ID 1, LINE_NO 1);
--   * FK_ORD_CUST added WITH NOCHECK at the end (untrusted).
SET NOCOUNT ON;
DECLARE @scale int = $(scale);
DECLARE @cust int = 1000 * @scale, @addr int = 1500 * @scale, @ord int = 3000 * @scale,
        @lines int = 9000 * @scale, @audit int = 5000 * @scale;
DECLARE @max int = 9000 * @scale;

CREATE TABLE #n (i int NOT NULL PRIMARY KEY);
WITH nums AS (
  SELECT TOP (@max) ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS i
  FROM sys.all_objects AS a CROSS JOIN sys.all_objects AS b CROSS JOIN sys.all_objects AS c)
INSERT INTO #n (i) SELECT CAST(i AS int) FROM nums;

INSERT INTO dbo.ORD_STATUS (STATUS_ID, STATUS_CD, STATUS_DESC)
VALUES (1, 'NEW', 'New order'), (2, 'PAID', 'Paid'), (3, 'SHIPPED', 'Shipped'), (4, 'CANCELLED', 'Cancelled');

SET IDENTITY_INSERT dbo.CUST ON;
INSERT INTO dbo.CUST (CUST_ID, CUST_NM, EMAIL_ADDR, PHONE_NO, DOB, CRT_DT, FAX_NO, NOTES)
SELECT n.i,
       CONCAT('First', n.i, ' Last', n.i),
       CASE WHEN n.i % 10 = 0 THEN NULL ELSE CONCAT('first', n.i, '.last', n.i, '@example.com') END,
       CASE WHEN n.i % 7 = 0 THEN NULL ELSE CONCAT('+1-555-', RIGHT(CONCAT('0000', n.i % 10000), 4)) END,
       CASE WHEN n.i % 5 = 0 THEN NULL ELSE DATEADD(DAY, (n.i * 37) % 18000, CAST('1950-01-01' AS datetime)) END,
       DATEADD(MINUTE, n.i * 17, CAST('2015-01-01' AS datetime)),
       CASE WHEN n.i % 50 = 0 THEN CONCAT('+1-555-9', RIGHT(CONCAT('000', n.i % 1000), 3)) ELSE NULL END,
       CASE WHEN n.i % 4 = 0 THEN CONCAT('Customer note ', n.i) ELSE NULL END
FROM #n AS n WHERE n.i <= @cust ORDER BY n.i;
SET IDENTITY_INSERT dbo.CUST OFF;

SET IDENTITY_INSERT dbo.ADDR ON;
INSERT INTO dbo.ADDR (ADDR_ID, CUST_ID, LINE1, CITY, ZIP, CTRY_CD)
SELECT n.i,
       CASE WHEN n.i <= @cust THEN n.i ELSE (n.i - @cust) * 2 - 1 END,
       CONCAT((n.i * 7) % 999 + 1, ' ', CHOOSE(n.i % 5 + 1, 'Main Street', 'High Street', 'Park Avenue', 'Oak Road', 'Mill Lane')),
       CHOOSE(n.i % 8 + 1, 'London', 'Paris', 'Berlin', 'Madrid', 'Rome', 'Dublin', 'Lisbon', 'Vienna'),
       CASE WHEN n.i % 20 = 0 THEN NULL ELSE RIGHT(CONCAT('00000', (n.i * 37) % 99999), 5) END,
       CHOOSE(n.i % 8 + 1, 'GB', 'FR', 'DE', 'ES', 'IT', 'IE', 'PT', 'AT')
FROM #n AS n WHERE n.i <= @addr ORDER BY n.i;
SET IDENTITY_INSERT dbo.ADDR OFF;

SET IDENTITY_INSERT dbo.PROD ON;
INSERT INTO dbo.PROD (PROD_ID, PROD_NM, PROD_DESC, UNIT_PRC, ACTIVE_FLG)
SELECT n.i,
       CONCAT('Product ', n.i),
       CASE WHEN n.i % 3 = 0 THEN NULL ELSE CONCAT('Description of product ', n.i) END,
       CAST(((n.i * 725) % 50000) / 100.0 + 0.99 AS money),
       CASE WHEN n.i % 10 = 0 THEN 'N' ELSE 'Y' END
FROM #n AS n WHERE n.i <= 200 ORDER BY n.i;
SET IDENTITY_INSERT dbo.PROD OFF;

SET IDENTITY_INSERT dbo.ORD_HDR ON;
INSERT INTO dbo.ORD_HDR (ORD_ID, CUST_ID, ORD_DT, STATUS_ID, SHIP_ADDR_ID, TOTAL_AMT, CMNT)
SELECT n.i,
       (n.i * 7) % @cust + 1,
       DATEADD(MINUTE, n.i * 53, CAST('2020-01-01' AS datetime)),
       n.i % 4 + 1,
       CASE WHEN n.i % 6 = 0 THEN NULL ELSE (n.i * 7) % @cust + 1 END,
       0,
       CASE WHEN n.i % 9 = 0 THEN 'Leave at reception' WHEN n.i % 11 = 0 THEN 'Gift wrap' ELSE NULL END
FROM #n AS n WHERE n.i <= @ord ORDER BY n.i;
-- anomalies: 2 orphan orders, 3 orders with a 300-character comment
INSERT INTO dbo.ORD_HDR (ORD_ID, CUST_ID, ORD_DT, STATUS_ID, SHIP_ADDR_ID, TOTAL_AMT, CMNT)
VALUES (@ord + 1, -1, CAST('2024-06-01T10:00:00' AS datetime), 1, NULL, 10.00, 'Orphan order 1'),
       (@ord + 2, -2, CAST('2024-06-02T10:00:00' AS datetime), 1, NULL, 10.00, 'Orphan order 2'),
       (@ord + 3, 1, CAST('2024-06-03T10:00:00' AS datetime), 2, 1, 10.00, LEFT(REPLICATE(CAST('Long comment. ' AS varchar(500)), 25), 300)),
       (@ord + 4, 2, CAST('2024-06-04T10:00:00' AS datetime), 2, 2, 10.00, LEFT(REPLICATE(CAST('Long comment. ' AS varchar(500)), 25), 300)),
       (@ord + 5, 3, CAST('2024-06-05T10:00:00' AS datetime), 2, 3, 10.00, LEFT(REPLICATE(CAST('Long comment. ' AS varchar(500)), 25), 300));
SET IDENTITY_INSERT dbo.ORD_HDR OFF;

INSERT INTO dbo.ORD_LINE (ORD_ID, LINE_NO, PROD_ID, QTY, UNIT_PRC)
SELECT (n.i - 1) / 3 + 1, (n.i - 1) % 3 + 1, p.PROD_ID, n.i % 5 + 1, p.UNIT_PRC
FROM #n AS n JOIN dbo.PROD AS p ON p.PROD_ID = (n.i * 13) % 200 + 1
WHERE n.i <= @lines;
INSERT INTO dbo.ORD_LINE (ORD_ID, LINE_NO, PROD_ID, QTY, UNIT_PRC)
SELECT @ord + 1, 1, PROD_ID, 1, UNIT_PRC FROM dbo.PROD WHERE PROD_ID = 1
UNION ALL
SELECT @ord + 2, 1, PROD_ID, 1, UNIT_PRC FROM dbo.PROD WHERE PROD_ID = 2;
UPDATE dbo.ORD_LINE SET QTY = 0 WHERE ORD_ID = 1 AND LINE_NO = 1;

UPDATE h SET TOTAL_AMT = t.AMT
FROM dbo.ORD_HDR AS h
JOIN (SELECT ORD_ID, SUM(QTY * UNIT_PRC) AS AMT FROM dbo.ORD_LINE GROUP BY ORD_ID) AS t ON t.ORD_ID = h.ORD_ID
WHERE h.ORD_ID <= @ord;

INSERT INTO dbo.AUDIT_LOG (LOG_TS, USR, ACTION_TXT)
SELECT DATEADD(SECOND, n.i * 37, CAST('2021-01-01' AS datetime)),
       CHOOSE(n.i % 5 + 1, 'admin', 'jsmith', 'mlee', 'svc_import', 'kpatel'),
       CHOOSE(n.i % 4 + 1, 'LOGIN', CONCAT('UPDATE CUST ', n.i % @cust + 1), CONCAT('CREATE ORDER ', n.i % @ord + 1), 'LOGOUT')
FROM #n AS n WHERE n.i <= @audit;

DROP TABLE #n;

ALTER TABLE dbo.ORD_HDR WITH NOCHECK ADD CONSTRAINT FK_ORD_CUST FOREIGN KEY (CUST_ID) REFERENCES dbo.CUST (CUST_ID);
