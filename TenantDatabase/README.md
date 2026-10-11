# Tenant database scripts - automatic

Every customer (tenant) gets its own database, created and kept up to date by the
Platform API with **no manual steps**. Just put `.sql` files here.

```
TenantDatabase/
  Schema/       tables, keys, indexes, views, procedures
                001_BusinessPanel_Schema.sql = the full Business Panel schema
  MasterData/   optional extra default records (normally empty - see below)
```

## Master data is copied by itself

No data script is needed. After the schema, the API copies the setup tables from the
**BusinessPanel** database on the same server (`TenantDatabase:TemplateDatabase`):

Roles, RolePermissions, MenuItems, EmailTemplates, FollowUpFrequencies, CustomerTypes,
PaymentMethods, Relationships, TrackerStatuses, QuotationStatuses, ServiceCategories,
ServiceSubCategories, Services.

* Same ids as BusinessPanel. Only rows whose key is missing are added - a tenant's own
  changes are never overwritten or deleted.
* Each table is copied once per tenant; **Re-seed master data** adds missing rows again
  (e.g. a new status you added in BusinessPanel).
* Not copied: customers, trackers, quotations, invoices, payments, follow-ups, documents,
  users, employees, ApplicationSettings and notification rules.
* Change the list with `TenantDatabase:TemplateTables` (parents first).

## What happens automatically

* **Provision tenant** creates `BP_<TENANTCODE>` on the same SQL Server as SuperAdminPanel,
  then runs every Schema script, copies the master data, then runs any MasterData script.
* **At API start-up and every 10 minutes** (`TenantDatabase:AutoMigrateMinutes`) every
  tenant database gets any script it has not had yet - so a new script reaches all
  customers by itself. Tenants without a database get one.
* Each tenant database has `dbo.SchemaVersions` (which scripts ran, with a checksum);
  `SuperAdminPanel.dbo.TenantDatabases` holds the database name, versions, step and last
  error, shown on the Tenant Control Center.

## Rules for scripts

1. Name them with a number first: `001_Initial_Tables.sql`, `002_Add_Quotation_Items.sql`.
   They run in name order.
2. **Schema** scripts run **once**. Never edit one that has already run - add a new
   numbered script instead (an edited one is reported on the Control Center, not re-run).
3. **MasterData** scripts run again whenever you change them, and on **Re-seed master data**,
   so write them re-runnable (`IF NOT EXISTS (...) INSERT ...` or `MERGE`).
4. No `USE` statements and no database names - each script runs inside the tenant database.
   `GO` lines are fine. A script runs in one transaction: if it fails, nothing of it stays
   and the error shows on the Control Center; it is retried on the next automatic run.
5. Do not put tenant-specific data here - only what every new customer starts with.

## Settings (appsettings.json, all optional)

```json
"TenantDatabase": {
  "Enabled": true,
  "DatabasePrefix": "BP_",
  "ScriptsPath": "",
  "BackupFolder": "",
  "AutoMigrateMinutes": 10,
  "TemplateDatabase": "BusinessPanel",
  "TemplateTables": []
}
```

The SQL login in `ConnectionStrings:DefaultConnection` needs permission to create databases
(`dbcreator`) - Windows login on a local SQL Server usually has it.
