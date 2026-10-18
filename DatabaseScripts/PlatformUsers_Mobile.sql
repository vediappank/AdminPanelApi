/*
  SuperAdminPanel: optional mobile number for Admin Panel (platform) users - unique, checked by the API
  (PlatformContactUniquenessService). The Admin Panel API also adds this column itself at startup;
  run this by hand only if its log says it could not (no ALTER permission).
*/
USE [SuperAdminPanel];
IF COL_LENGTH(N'dbo.PlatformUsers', N'Mobile') IS NULL
    ALTER TABLE dbo.PlatformUsers ADD Mobile NVARCHAR(30) NULL;
GO
