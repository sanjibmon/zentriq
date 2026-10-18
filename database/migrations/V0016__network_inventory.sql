-- V0016: Network inventory (switches, routers, firewalls, access points, controllers): ports, neighbours (LLDP/CDP),
-- VLANs, MAC address and ARP tables, modules, PoE — and what is connected to each switch port, including switches
-- that have no management (inferred from the MAC address table).
SET search_path = zentriq, public;

CREATE TABLE network_devices (
    asset_id        uuid PRIMARY KEY REFERENCES assets(id) ON DELETE CASCADE,
    snmp_version    text,
    ports_total     integer NOT NULL DEFAULT 0,      -- physical (Ethernet) ports
    ports_up        integer NOT NULL DEFAULT 0,
    ports_admin_down integer NOT NULL DEFAULT 0,
    interfaces      integer NOT NULL DEFAULT 0,      -- all interfaces (VLAN, LAG, tunnel, loopback …)
    vlans           integer NOT NULL DEFAULT 0,
    neighbors       integer NOT NULL DEFAULT 0,
    mac_entries     integer NOT NULL DEFAULT 0,
    arp_entries     integer NOT NULL DEFAULT 0,
    ip_forwarding   boolean,
    routes          bigint,
    cpu_percent     integer,
    poe_budget_w    integer,
    poe_used_w      integer,
    stp_root        boolean,
    stack_members   integer,
    bridge_address  text,
    collected_at    timestamptz NOT NULL DEFAULT now()
);

CREATE TABLE network_interfaces (
    asset_id         uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    if_index         integer NOT NULL,
    name             text,
    description      text,
    alias            text,
    type             text,                            -- ethernet | wireless | lag | vlan | loopback | tunnel | other
    mac              text,
    admin            text,
    oper             text,
    speed_mbps       bigint,
    duplex           text,
    mtu              integer,
    vlan             integer,
    trunk            boolean,
    poe              boolean,
    in_errors        bigint,
    out_errors       bigint,
    ipv4             text[] NOT NULL DEFAULT '{}',
    last_change_sec  bigint,
    -- Derived (refreshed after every collection and periodically):
    mac_count        integer NOT NULL DEFAULT 0,      -- devices (MAC addresses) learned on the port
    connected        jsonb NOT NULL DEFAULT '[]',     -- [{assetId, name, ip, mac}] known devices on the port (up to 10)
    neighbor         text,                            -- LLDP/CDP neighbour on the port ("SW-ACCESS-2 Gi1/0/48")
    neighbor_asset_id uuid REFERENCES assets(id) ON DELETE SET NULL,
    uplink           boolean NOT NULL DEFAULT false,  -- leads to other network equipment
    unmanaged_suspect boolean NOT NULL DEFAULT false, -- several devices, no neighbour, not a trunk: an unmanaged switch or hub
    PRIMARY KEY (asset_id, if_index)
);
CREATE INDEX ix_network_interfaces_suspect ON network_interfaces (asset_id) WHERE unmanaged_suspect;

CREATE TABLE network_neighbors (
    id                 bigserial PRIMARY KEY,
    asset_id           uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    protocol           text NOT NULL CHECK (protocol IN ('lldp','cdp')),
    local_port         text,
    local_if_index     integer,
    remote_name        text,
    remote_port        text,
    remote_description text,
    remote_platform    text,
    remote_chassis_id  text,
    remote_address     inet,
    capabilities       text[] NOT NULL DEFAULT '{}',
    remote_asset_id    uuid REFERENCES assets(id) ON DELETE SET NULL
);
CREATE INDEX ix_network_neighbors_asset ON network_neighbors (asset_id);
CREATE INDEX ix_network_neighbors_remote ON network_neighbors (remote_asset_id);

CREATE TABLE network_vlans (
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    vlan_id   integer NOT NULL,
    name      text,
    PRIMARY KEY (asset_id, vlan_id)
);

CREATE TABLE network_mac_table (
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    mac       macaddr NOT NULL,
    vlan      integer NOT NULL DEFAULT 0,
    if_index  integer,
    PRIMARY KEY (asset_id, mac, vlan)
);
CREATE INDEX ix_network_mac_table_mac ON network_mac_table (mac);

CREATE TABLE network_arp (
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    ip        inet NOT NULL,
    mac       macaddr NOT NULL,
    if_index  integer,
    PRIMARY KEY (asset_id, ip)
);
CREATE INDEX ix_network_arp_mac ON network_arp (mac);

CREATE TABLE network_modules (
    id        bigserial PRIMARY KEY,
    asset_id  uuid NOT NULL REFERENCES assets(id) ON DELETE CASCADE,
    class     text NOT NULL,
    name      text,
    model     text,
    serial    text,
    firmware  text
);
CREATE INDEX ix_network_modules_asset ON network_modules (asset_id);


-- Built-in network reports.
INSERT INTO report_definitions (name, dataset, shared, system, description, definition) VALUES
 ('Network device register', 'network_devices', true, true, 'Every switch, router, firewall and access point: model, serial, firmware, management, ports and location.',
  '{"columns":["hostname","ip","type","manufacturer","model","serial","firmware","management","location","ports_total","ports_up","port_use_pct","vlans","last_seen"],"filters":[],"sort":[{"column":"type","dir":"asc"},{"column":"hostname","dir":"asc"}]}'),
 ('Network devices by type and management', 'network_devices', true, true, 'Number of network devices per type and how they are managed (SNMP, web interface only, none).',
  '{"columns":["type","management"],"groupBy":["type","management"],"filters":[],"sort":[{"column":"count","dir":"desc"}],"chart":"bar"}'),
 ('Network firmware by model', 'network_devices', true, true, 'Firmware / software versions in use per manufacturer and model — find devices to update.',
  '{"columns":["manufacturer","model","firmware"],"groupBy":["manufacturer","model","firmware"],"filters":[],"sort":[{"column":"manufacturer","dir":"asc"},{"column":"model","dir":"asc"}]}'),
 ('Switch port capacity', 'network_devices', true, true, 'Ports, ports in use and free ports per switch; PoE budget and use.',
  '{"columns":["hostname","model","ports_total","ports_up","ports_free","port_use_pct","ports_disabled","poe_budget","poe_used"],"filters":[{"column":"type","op":"eq","value":"switch"}],"sort":[{"column":"port_use_pct","dir":"desc"}]}'),
 ('Switch port map', 'network_ports', true, true, 'Which device is connected to which switch port, with VLAN, speed and PoE.',
  '{"columns":["hostname","port","port_description","oper_status","speed","duplex","vlan","poe","connected","neighbor","mac_count"],"filters":[{"column":"oper_status","op":"eq","value":"up"}],"sort":[{"column":"hostname","dir":"asc"},{"column":"port_index","dir":"asc"}]}'),
 ('Suspected unmanaged switches', 'network_ports', true, true, 'Access ports with several devices behind them and no LLDP/CDP neighbour: an unmanaged switch, hub or unknown access point.',
  '{"columns":["hostname","port","port_description","vlan","mac_count","connected"],"filters":[{"column":"unmanaged","op":"eq","value":"yes"}],"sort":[{"column":"mac_count","dir":"desc"}]}'),
 ('Port errors and duplex mismatches', 'network_ports', true, true, 'Ports with errors or running half duplex.',
  '{"columns":["hostname","port","oper_status","speed","duplex","errors","connected"],"filters":[{"column":"errors","op":"gt","value":"0"}],"sort":[{"column":"errors","dir":"desc"}]}'),
 ('Network topology (LLDP/CDP links)', 'network_neighbors', true, true, 'Links between network devices as reported by LLDP and CDP.',
  '{"columns":["hostname","local_port","remote_name","remote_port","remote_platform","remote_ip","protocol","remote_inventoried"],"filters":[],"sort":[{"column":"hostname","dir":"asc"},{"column":"local_port","dir":"asc"}]}');
