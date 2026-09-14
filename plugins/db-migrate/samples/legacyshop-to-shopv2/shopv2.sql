-- ShopV2: target side of the db-migrate sample pair (schema app).
-- Run against an existing, empty database. Batches are separated by GO.
CREATE SCHEMA app;
GO
SET NOCOUNT ON;

CREATE TABLE app.Customers (
  CustomerId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Customers PRIMARY KEY,
  FirstName nvarchar(50) NOT NULL,
  LastName nvarchar(50) NOT NULL,
  Email nvarchar(120) NULL,
  Phone nvarchar(30) NULL,
  BirthDate date NULL,
  CreatedAt datetime2(0) NOT NULL CONSTRAINT DF_Customers_CreatedAt DEFAULT (sysutcdatetime()),
  PrimaryAddressId int NULL,
  Notes nvarchar(max) NULL,
  DisplayName AS (FirstName + N' ' + LastName));

CREATE TABLE app.Addresses (
  AddressId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Addresses PRIMARY KEY,
  CustomerId int NOT NULL CONSTRAINT FK_Addresses_Customers REFERENCES app.Customers (CustomerId),
  Line1 nvarchar(200) NOT NULL,
  City nvarchar(80) NOT NULL,
  PostalCode nvarchar(12) NULL,
  CountryCode char(2) NOT NULL);

ALTER TABLE app.Customers ADD CONSTRAINT FK_Customers_PrimaryAddress
  FOREIGN KEY (PrimaryAddressId) REFERENCES app.Addresses (AddressId);

CREATE TABLE app.Products (
  ProductId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Products PRIMARY KEY,
  Name nvarchar(150) NOT NULL,
  Description nvarchar(500) NULL,
  UnitPrice decimal(19,4) NOT NULL,
  IsActive bit NOT NULL,
  RowVer rowversion NOT NULL);

CREATE TABLE app.Orders (
  OrderId int IDENTITY(1,1) NOT NULL CONSTRAINT PK_Orders PRIMARY KEY,
  CustomerId int NOT NULL CONSTRAINT FK_Orders_Customers REFERENCES app.Customers (CustomerId),
  OrderDate datetime2(0) NOT NULL,
  StatusCode varchar(10) NOT NULL,
  ShippingAddressId int NULL CONSTRAINT FK_Orders_Addresses REFERENCES app.Addresses (AddressId),
  TotalAmount decimal(19,4) NOT NULL,
  Comment nvarchar(200) NULL);

CREATE TABLE app.OrderLines (
  OrderId int NOT NULL CONSTRAINT FK_OrderLines_Orders REFERENCES app.Orders (OrderId),
  LineNumber smallint NOT NULL,
  ProductId int NOT NULL CONSTRAINT FK_OrderLines_Products REFERENCES app.Products (ProductId),
  Quantity int NOT NULL CONSTRAINT CK_OrderLines_Quantity CHECK (Quantity > 0),
  UnitPrice decimal(19,4) NOT NULL,
  CONSTRAINT PK_OrderLines PRIMARY KEY (OrderId, LineNumber));

CREATE TABLE app.AuditEvents (
  EventTime datetime2(3) NOT NULL,
  UserName nvarchar(50) NOT NULL,
  Action nvarchar(200) NOT NULL);
GO
CREATE TRIGGER app.trg_Orders_Audit ON app.Orders AFTER INSERT AS BEGIN SET NOCOUNT ON; END;
GO
