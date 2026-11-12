-- V0044 (build 136): Patch Management keeps operating-system updates and software updates apart.
--  kind: os         Windows itself (cumulative, servicing stack, .NET Framework, Defender) and Linux packages
--        microsoft  other Microsoft software offered by Windows / Microsoft Update or WSUS (Office, SQL Server, Edge …)
--        thirdparty applications with a newer version in winget (Chrome, Zoom, 7-Zip …), id winget:<package id>:<version>
ALTER TABLE patches ADD COLUMN kind text NOT NULL DEFAULT 'os' CHECK (kind IN ('os','microsoft','thirdparty'));
ALTER TABLE patches DROP CONSTRAINT IF EXISTS patches_classification_check;
ALTER TABLE patches ADD CONSTRAINT patches_classification_check
    CHECK (classification IN ('critical','security','definition','updates','update-rollup','feature-pack','service-pack','driver','tools','upgrade','application','other'));
CREATE INDEX ix_patches_kind ON patches (kind);

UPDATE patches SET kind = 'microsoft'
 WHERE platform = 'windows' AND product IS NOT NULL
   AND product !~* '^(windows|microsoft defender|windows defender|\.net framework|microsoft server operating system)'
   AND product ~* '(office|microsoft 365|sql server|visual studio|exchange|sharepoint|skype|teams|edge|silverlight|dynamics|project|visio|system center|azure|onedrive|lync|developer tools|asp\.net|\.net( [0-9]|$| core))';
