-- V0020: the device's IPv4 address for lists (Agents, Remote Support devices): its primary address when that is IPv4,
-- otherwise the first usable IPv4 address of its inventoried network adapters (adapters with a gateway first).
SET search_path = zentriq, public;

CREATE OR REPLACE FUNCTION asset_ipv4(p_asset uuid) RETURNS text
LANGUAGE sql STABLE AS $$
    SELECT COALESCE(
        (SELECT host(a.primary_ip) FROM zentriq.assets a WHERE a.id = p_asset AND family(a.primary_ip) = 4),
        (SELECT ip
           FROM zentriq.asset_inventory_documents d,
                LATERAL jsonb_array_elements(COALESCE(d.document->'network', '[]'::jsonb)) WITH ORDINALITY AS nic(adapter, n),
                LATERAL jsonb_array_elements_text(COALESCE(nic.adapter->'ipv4', '[]'::jsonb)) AS ip
          WHERE d.asset_id = p_asset
            AND ip ~ '^\d{1,3}(\.\d{1,3}){3}$' AND ip NOT LIKE '169.254.%' AND ip NOT LIKE '127.%'
          ORDER BY (COALESCE(nic.adapter->>'gateway', '') = '') , nic.n
          LIMIT 1))
$$;
