/* 006 - Website accounts: username + password login (extends dbo.WebsiteSubscribers, no new account table).
   Existing OTP accounts keep working; a person can add a username / password in My account or by signing up again.
   Safe to run again. */

IF COL_LENGTH('dbo.WebsiteSubscribers', 'Username') IS NULL
    ALTER TABLE dbo.WebsiteSubscribers ADD Username NVARCHAR(40) NULL;
IF COL_LENGTH('dbo.WebsiteSubscribers', 'PasswordHash') IS NULL
    ALTER TABLE dbo.WebsiteSubscribers ADD PasswordHash VARCHAR(200) NULL;      -- pbkdf2$iterations$salt$hash
IF COL_LENGTH('dbo.WebsiteSubscribers', 'PasswordSetAt') IS NULL
    ALTER TABLE dbo.WebsiteSubscribers ADD PasswordSetAt DATETIME2(0) NULL;
IF COL_LENGTH('dbo.WebsiteSubscribers', 'FailedLogins') IS NULL
    ALTER TABLE dbo.WebsiteSubscribers ADD FailedLogins INT NOT NULL CONSTRAINT DF_WebsiteSubscribers_FailedLogins DEFAULT (0);
IF COL_LENGTH('dbo.WebsiteSubscribers', 'LockedUntil') IS NULL
    ALTER TABLE dbo.WebsiteSubscribers ADD LockedUntil DATETIME2(0) NULL;
IF COL_LENGTH('dbo.SubscriberChallenges', 'PendingPasswordHash') IS NULL
    ALTER TABLE dbo.SubscriberChallenges ADD PendingPasswordHash VARCHAR(200) NULL;   -- applied only after the email code is right
IF COL_LENGTH('dbo.SubscriberChallenges', 'PendingUsername') IS NULL
    ALTER TABLE dbo.SubscriberChallenges ADD PendingUsername NVARCHAR(40) NULL;
GO

IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = 'UX_WebsiteSubscribers_Username' AND object_id = OBJECT_ID('dbo.WebsiteSubscribers'))
    CREATE UNIQUE INDEX UX_WebsiteSubscribers_Username ON dbo.WebsiteSubscribers (Username) WHERE Username IS NOT NULL;
GO
