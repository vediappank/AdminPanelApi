/*
  REPORT ONLY - nothing is changed. SuperAdminPanel (Admin Panel database):
    1. tenants sharing a contact e-mail or contact mobile
    2. platform users sharing an e-mail or mobile
    3. e-mails that are BOTH an Admin Panel login and a Business Panel login (dbo.TenantUsers)
  Compared the same way the API compares them: e-mail ignoring case/spaces; mobile ignoring formatting,
  leading zeros and the country code (971 966 974 968 965 973 91 44 1).
  New duplicates can no longer be saved; clean these up by changing all but one record of each group.
*/
USE [SuperAdminPanel];
SET NOCOUNT ON;

IF OBJECT_ID(N'tempdb..#C') IS NOT NULL DROP TABLE #C;
CREATE TABLE #C (Kind NVARCHAR(20), Id INT, Name NVARCHAR(400), Email NVARCHAR(256), Mobile NVARCHAR(60));
INSERT #C SELECT N'Tenant', TenantId, TenantName + N' (' + TenantCode + N')', ContactEmail, ContactMobile FROM dbo.Tenants;
IF COL_LENGTH(N'dbo.PlatformUsers', N'Mobile') IS NOT NULL
    EXEC (N'INSERT #C SELECT N''PlatformUser'', PlatformUserId, FirstName + ISNULL(N'' '' + LastName, N''''), Email, Mobile FROM dbo.PlatformUsers;');
ELSE
    INSERT #C SELECT N'PlatformUser', PlatformUserId, FirstName + ISNULL(N' ' + LastName, N''), Email, NULL FROM dbo.PlatformUsers;

;WITH k AS
(
    SELECT c.*, NULLIF(LOWER(LTRIM(RTRIM(c.Email))), N'') AS EmailKey, m3.MobileKey
    FROM #C c
    CROSS APPLY (SELECT REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(REPLACE(ISNULL(c.Mobile, N''), N' ', N''), N'-', N''), N'+', N''), N'(', N''), N')', N''), N'.', N'') AS D) m1
    CROSS APPLY (SELECT SUBSTRING(m1.D, PATINDEX(N'%[^0]%', m1.D + N'x'), 60) AS D) m2
    CROSS APPLY (SELECT CASE WHEN LEN(x.D) >= 6 THEN x.D END AS MobileKey
        FROM (SELECT SUBSTRING(y.D, PATINDEX(N'%[^0]%', y.D + N'x'), 60) AS D
              FROM (SELECT CASE
                        WHEN LEN(m2.D) >= 11 AND LEFT(m2.D, 3) IN (N'971', N'966', N'974', N'968', N'965', N'973') THEN STUFF(m2.D, 1, 3, N'')
                        WHEN LEN(m2.D) >= 11 AND LEFT(m2.D, 2) IN (N'91', N'44') THEN STUFF(m2.D, 1, 2, N'')
                        WHEN LEN(m2.D) >= 11 AND LEFT(m2.D, 1) = N'1' THEN STUFF(m2.D, 1, 1, N'')
                        ELSE m2.D END AS D) y) x) m3
)
SELECT N'E-mail' AS Field, k.EmailKey AS Value, k.Kind, k.Id, k.Name, k.Email AS AsEntered,
       COUNT(*) OVER (PARTITION BY k.Kind, k.EmailKey) AS UsedTimes
INTO #Dup
FROM k WHERE k.EmailKey IS NOT NULL
UNION ALL
SELECT N'Mobile', k.MobileKey, k.Kind, k.Id, k.Name, k.Mobile, COUNT(*) OVER (PARTITION BY k.Kind, k.MobileKey)
FROM k WHERE k.MobileKey IS NOT NULL;

-- 1 + 2: duplicates among tenants / among platform users
SELECT Kind, Field, Value, Id, Name, AsEntered, UsedTimes
FROM #Dup WHERE UsedTimes > 1
ORDER BY Kind, Field, Value, Id;

-- 3: one e-mail used for an Admin Panel login AND a Business Panel login
IF OBJECT_ID(N'dbo.TenantUsers', N'U') IS NOT NULL
    SELECT p.PlatformUserId, p.Email AS AdminPanelLogin, t.TenantName + N' (' + t.TenantCode + N')' AS BusinessPanelLoginOf
    FROM dbo.PlatformUsers p
    JOIN dbo.TenantUsers u ON u.Email = LOWER(LTRIM(RTRIM(p.Email)))
    JOIN dbo.Tenants t ON t.TenantId = u.TenantId
    ORDER BY p.Email;

DROP TABLE #Dup;
DROP TABLE #C;
