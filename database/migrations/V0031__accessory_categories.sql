-- V0031: IT accessory categories as a managed list (IT Accessories › Categories): the built-in categories plus the ones
-- administrators add. Accessories keep the category name; renaming a category renames it on its accessories.
SET search_path = zentriq, public;

CREATE TABLE accessory_categories (
    id         uuid PRIMARY KEY DEFAULT gen_random_uuid(),
    name       text NOT NULL CHECK (length(btrim(name)) BETWEEN 1 AND 60),
    built_in   boolean NOT NULL DEFAULT false,
    created_at timestamptz NOT NULL DEFAULT now()
);
CREATE UNIQUE INDEX ux_accessory_categories_name ON accessory_categories (lower(name));

INSERT INTO accessory_categories (name, built_in)
SELECT n, true FROM unnest(ARRAY['Mouse', 'Keyboard', 'Keyboard and mouse set', 'Headset', 'Webcam', 'Docking station', 'Monitor cable',
    'Adapter / dongle', 'Charger / power adapter', 'USB drive', 'Laptop bag', 'Mouse pad', 'Speaker', 'Battery', 'Other']) AS n
ON CONFLICT DO NOTHING;

-- Categories typed on existing accessories become part of the list.
INSERT INTO accessory_categories (name)
SELECT DISTINCT ON (lower(btrim(category))) btrim(category) FROM accessory_items WHERE btrim(category) <> ''
ON CONFLICT DO NOTHING;
