SuperAdminPanel database changes - applied automatically by the Admin API when it starts.

HOW TO ADD A DATABASE CHANGE
  1. Create a new file here with the next number:   004_Short_Description.sql
     (3+ digits, underscore, letters/digits/_ only, .sql)
  2. Write it so it can safely run again (IF NOT EXISTS / IF COL_LENGTH(...) IS NULL ...).
     "GO" on its own line splits batches, like in SSMS. Do not add USE [database].
  3. Test it: run the Admin API on your PC - the log shows "DB MIGRATIONS -> 004_... applied".
  4. Commit, then deploy.cmd > 2 (Admin Panel + API). The API restart on the server runs it once on
     production. Check Admin Panel > Settings > Deployments > Database scripts.

RULES
  * Each script runs once, in number order, in one transaction (all or nothing).
  * Done scripts are listed in dbo.SchemaMigrations. Never edit a script that already ran - add a
    new number instead (an edited script is reported as "changed", not run again).
  * A failing script is rolled back and logged; later scripts wait. The API keeps running.
    Fix the script and restart the API (or deploy again).
  * Data deletes and report-only scripts do NOT belong here (Truncate_*, Find_Duplicate_*) - run
    those by hand in SSMS.
  * Back up SuperAdminPanel before deploying a script that changes existing data.
  * Switch off: appsettings "DatabaseMigrations": { "Enabled": false }.

Scripts before 001 (Branding, Subscriptions, Messaging, Chatbot, Website Leads / Settings, Fix_Menus ...)
were run by hand on production (Deployment\scripts\README-deploy.txt) and are not repeated here.
