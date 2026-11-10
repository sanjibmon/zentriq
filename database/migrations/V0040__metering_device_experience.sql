-- V0040 (build 120): software metering and Device Experience.
--  * software_usage: per device, day, program and user — minutes running and launches, reported by the agent.
--  * sam_products.exe_patterns: the programs (file names, wildcards) that count as using the product.
--  * device_experience*: resource pressure per day, events (boots, crashes, hangs, blue screens, unexpected shutdowns,
--    service failures, out-of-memory kills) and the score per device, with a daily history for the trend.

CREATE TABLE software_usage (
    asset_id   uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    day        date NOT NULL,
    path       text NOT NULL,
    exe        text NOT NULL,
    product    text,
    publisher  text,
    version    text,
    user_name  text NOT NULL DEFAULT '',
    minutes    integer NOT NULL DEFAULT 0 CHECK (minutes >= 0),
    launches   integer NOT NULL DEFAULT 0 CHECK (launches >= 0),
    PRIMARY KEY (asset_id, day, path, user_name)
);
CREATE INDEX ix_software_usage_exe ON software_usage (lower(exe), day DESC);
CREATE INDEX ix_software_usage_day ON software_usage (day);

-- Devices that meter, and since when (a program without use since then was not used).
CREATE TABLE software_usage_devices (
    asset_id     uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    since        date NOT NULL,
    reported_at  timestamptz NOT NULL DEFAULT now()
);

ALTER TABLE sam_products ADD COLUMN exe_patterns text[] NOT NULL DEFAULT '{}';

CREATE TABLE device_experience_days (
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    day       date NOT NULL,
    samples   integer NOT NULL DEFAULT 0,
    cpu_avg   real,
    cpu_high  integer NOT NULL DEFAULT 0,
    mem_avg   real,
    mem_high  integer NOT NULL DEFAULT 0,
    PRIMARY KEY (asset_id, day)
);

CREATE TABLE device_experience_events (
    id        bigserial PRIMARY KEY,
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    at        timestamptz NOT NULL,
    kind      text NOT NULL CHECK (kind IN ('boot','crash','hang','bugcheck','shutdown','service','oom')),
    app       text,
    version   text,
    detail    text,
    ms        bigint,
    ms2       bigint
);
CREATE UNIQUE INDEX ux_device_experience_events ON device_experience_events (asset_id, kind, at, COALESCE(app, ''));
CREATE INDEX ix_device_experience_events_kind ON device_experience_events (kind, at DESC);

CREATE TABLE device_experience (
    asset_id            uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    score               integer NOT NULL CHECK (score BETWEEN 0 AND 100),
    rating              text NOT NULL CHECK (rating IN ('good','fair','poor')),
    boot_ms             bigint,
    logon_ms            bigint,
    crashes             integer NOT NULL DEFAULT 0,
    hangs               integer NOT NULL DEFAULT 0,
    bugchecks           integer NOT NULL DEFAULT 0,
    shutdowns           integer NOT NULL DEFAULT 0,
    service_failures    integer NOT NULL DEFAULT 0,
    cpu_high_percent    real,
    mem_high_percent    real,
    system_free_percent real,
    battery_health      real,
    uptime_days         real,
    issues              jsonb NOT NULL DEFAULT '[]',
    computed_at         timestamptz NOT NULL DEFAULT now()
);
CREATE INDEX ix_device_experience_score ON device_experience (score);

CREATE TABLE device_experience_history (
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    day       date NOT NULL,
    score     integer NOT NULL,
    PRIMARY KEY (asset_id, day)
);

-- Products added from the licence catalogue before build 120: the programs that count as using them.
UPDATE sam_products SET exe_patterns = ARRAY['winword.exe','excel.exe','powerpnt.exe','outlook.exe','onenote.exe','msaccess.exe','mspub.exe'] WHERE catalog_key = 'catalog:ms-office' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['winword.exe','excel.exe','powerpnt.exe','outlook.exe','onenote.exe','msaccess.exe','mspub.exe'] WHERE catalog_key = 'catalog:ms-365-apps' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['visio.exe'] WHERE catalog_key = 'catalog:ms-visio' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['winproj.exe'] WHERE catalog_key = 'catalog:ms-project' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['devenv.exe'] WHERE catalog_key = 'catalog:ms-visual-studio' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['acrobat.exe'] WHERE catalog_key = 'catalog:adobe-acrobat' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['creative cloud.exe'] WHERE catalog_key = 'catalog:adobe-creative-cloud' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['photoshop.exe'] WHERE catalog_key = 'catalog:adobe-photoshop' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['illustrator.exe'] WHERE catalog_key = 'catalog:adobe-illustrator' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['indesign.exe'] WHERE catalog_key = 'catalog:adobe-indesign' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['adobe premiere pro.exe'] WHERE catalog_key = 'catalog:adobe-premiere' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['afterfx.exe'] WHERE catalog_key = 'catalog:adobe-after-effects' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['acad.exe'] WHERE catalog_key = 'catalog:autocad' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['acadlt.exe'] WHERE catalog_key = 'catalog:autocad-lt' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['revit.exe'] WHERE catalog_key = 'catalog:revit' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['3dsmax.exe'] WHERE catalog_key = 'catalog:3ds-max' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['inventor.exe'] WHERE catalog_key = 'catalog:inventor' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['sldworks.exe'] WHERE catalog_key = 'catalog:solidworks' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['sketchup.exe'] WHERE catalog_key = 'catalog:sketchup' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['revu.exe'] WHERE catalog_key = 'catalog:bluebeam' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['matlab.exe'] WHERE catalog_key = 'catalog:matlab' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['stats.exe'] WHERE catalog_key = 'catalog:spss' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['mtb.exe','minitab.exe'] WHERE catalog_key = 'catalog:minitab' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['tableau.exe'] WHERE catalog_key = 'catalog:tableau-desktop' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['idea64.exe','idea.exe'] WHERE catalog_key = 'catalog:intellij-ultimate' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['pycharm64.exe','pycharm.exe'] WHERE catalog_key = 'catalog:pycharm-pro' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['rider64.exe','rider.exe'] WHERE catalog_key = 'catalog:jetbrains-rider' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['datagrip64.exe','datagrip.exe'] WHERE catalog_key = 'catalog:datagrip' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['navicat.exe'] WHERE catalog_key = 'catalog:navicat' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['bcompare.exe'] WHERE catalog_key = 'catalog:beyond-compare' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['sublime_text.exe'] WHERE catalog_key = 'catalog:sublime-text' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['winrar.exe'] WHERE catalog_key = 'catalog:winrar' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['teamviewer.exe'] WHERE catalog_key = 'catalog:teamviewer' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['anydesk.exe'] WHERE catalog_key = 'catalog:anydesk' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['camtasia.exe','camtasiastudio.exe'] WHERE catalog_key = 'catalog:camtasia' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['snagiteditor.exe','snagit32.exe'] WHERE catalog_key = 'catalog:snagit' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['foxitpdfeditor.exe','foxitphantompdf.exe'] WHERE catalog_key = 'catalog:foxit-editor' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['nitropdf.exe'] WHERE catalog_key = 'catalog:nitro-pdf' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['finereader.exe'] WHERE catalog_key = 'catalog:abbyy-finereader' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['coreldrw.exe'] WHERE catalog_key = 'catalog:coreldraw' AND cardinality(exe_patterns) = 0;
UPDATE sam_products SET exe_patterns = ARRAY['vmware.exe'] WHERE catalog_key = 'catalog:vmware-workstation' AND cardinality(exe_patterns) = 0;
