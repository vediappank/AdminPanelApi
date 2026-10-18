/*
  Users.Mobile - optional mobile number for Business Panel users (unique within the company, checked by
  the API - see Services/ContactUniquenessService.cs).

  Same script in two places:
    BusinessPanelApi/database/2026-10-17_users_mobile.sql        run by hand on the shared BusinessPanel database
    AdminPanelApi/TenantDatabase/Schema/003_Users_Mobile.sql     run automatically on every tenant database (BP_*)
                                                                 by the Admin Panel's migration worker
  Safe to run more than once. The API works before and after it: Mobile is only sent / shown once it exists.
*/
IF COL_LENGTH(N'dbo.Users', N'Mobile') IS NULL
    ALTER TABLE dbo.Users ADD Mobile NVARCHAR(30) NULL;
GO

CREATE OR ALTER PROCEDURE [dbo].[usp_Users_Insert]
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Email NVARCHAR(200),
    @PasswordHash NVARCHAR(500),
    @Status NVARCHAR(20),
    @RoleId INT,
    @Mobile NVARCHAR(30) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    INSERT INTO [dbo].[Users] ([FirstName], [LastName], [Email], [Mobile], [PasswordHash], [Status], [RoleId], [CreatedDate])
    VALUES (@FirstName, @LastName, @Email, NULLIF(LTRIM(RTRIM(@Mobile)), N''), @PasswordHash, @Status, @RoleId, SYSUTCDATETIME());

    DECLARE @NewId INT = CONVERT(INT, SCOPE_IDENTITY());
    SELECT u.*, r.RoleName
    FROM [dbo].[Users] u LEFT JOIN [dbo].[Roles] r ON r.RoleId=u.RoleId
    WHERE u.UserId=@NewId;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[usp_Users_Update]
    @Id INT,
    @FirstName NVARCHAR(100),
    @LastName NVARCHAR(100),
    @Email NVARCHAR(200),
    @PasswordHash NVARCHAR(500),
    @Status NVARCHAR(20),
    @RoleId INT,
    @Mobile NVARCHAR(30) = NULL
AS
BEGIN
    SET NOCOUNT ON;
    UPDATE [dbo].[Users]
    SET [FirstName] = @FirstName, [LastName] = @LastName, [Email] = @Email, [Mobile] = NULLIF(LTRIM(RTRIM(@Mobile)), N''),
        [PasswordHash] = COALESCE(@PasswordHash, [PasswordHash]), [Status] = @Status, [RoleId] = @RoleId, [ModifiedDate] = SYSUTCDATETIME()
    WHERE [UserId] = @Id;

    IF @@ROWCOUNT = 0 RETURN;
    SELECT u.*, r.RoleName
    FROM [dbo].[Users] u LEFT JOIN [dbo].[Roles] r ON r.RoleId=u.RoleId
    WHERE u.UserId=@Id;
END
GO

CREATE OR ALTER PROCEDURE [dbo].[usp_Users_GetPaged]
    @PageNumber INT = 1,
    @PageSize INT = 25
AS
BEGIN
    SET NOCOUNT ON;
    SET @PageNumber = CASE WHEN @PageNumber < 1 THEN 1 ELSE @PageNumber END;
    SET @PageSize = CASE WHEN @PageSize < 1 OR @PageSize > 200 THEN 25 ELSE @PageSize END;
    DECLARE @TotalCount INT;

    SELECT @TotalCount = COUNT(1) FROM [dbo].[Users];
    SELECT @TotalCount AS TotalCount;

    SELECT [UserId], [FirstName], [LastName], [Email], [Mobile], [PasswordHash], [Status], [RoleId], [CreatedDate], [ModifiedDate]
    FROM [dbo].[Users]
    ORDER BY [UserId]
    OFFSET (@PageNumber - 1) * @PageSize ROWS
    FETCH NEXT @PageSize ROWS ONLY;
END
GO
