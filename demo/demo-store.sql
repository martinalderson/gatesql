-- Demo ecommerce store schema and seed data for GateSQL demos.
-- ~50 customers, ~40 products, ~200 orders, ~500 order items, reviews, coupons, and more.
-- Usage: psql -h localhost -p 5432 -U postgres -d postgres -f demo/demo-store.sql

BEGIN;

-- ============================================================
-- Schema
-- ============================================================

CREATE TABLE IF NOT EXISTS customers (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL,
    email TEXT NOT NULL UNIQUE,
    phone TEXT,
    city TEXT,
    country TEXT NOT NULL DEFAULT 'US',
    tier TEXT NOT NULL DEFAULT 'standard',
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS categories (
    id SERIAL PRIMARY KEY,
    name TEXT NOT NULL UNIQUE,
    parent_id INT REFERENCES categories(id)
);

CREATE TABLE IF NOT EXISTS products (
    id SERIAL PRIMARY KEY,
    sku TEXT NOT NULL UNIQUE,
    name TEXT NOT NULL,
    category_id INT NOT NULL REFERENCES categories(id),
    price NUMERIC(10, 2) NOT NULL,
    cost NUMERIC(10, 2) NOT NULL,
    weight_kg NUMERIC(6, 2),
    is_active BOOLEAN NOT NULL DEFAULT true,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS inventory (
    id SERIAL PRIMARY KEY,
    product_id INT NOT NULL REFERENCES products(id) UNIQUE,
    quantity INT NOT NULL DEFAULT 0,
    reorder_threshold INT NOT NULL DEFAULT 10,
    warehouse TEXT NOT NULL DEFAULT 'us-east',
    last_restocked_at TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS coupons (
    id SERIAL PRIMARY KEY,
    code TEXT NOT NULL UNIQUE,
    discount_pct NUMERIC(5, 2) NOT NULL,
    min_order NUMERIC(10, 2) NOT NULL DEFAULT 0,
    max_uses INT,
    times_used INT NOT NULL DEFAULT 0,
    valid_from TIMESTAMPTZ NOT NULL DEFAULT now(),
    valid_until TIMESTAMPTZ NOT NULL DEFAULT now() + INTERVAL '30 days',
    is_active BOOLEAN NOT NULL DEFAULT true
);

CREATE TABLE IF NOT EXISTS orders (
    id SERIAL PRIMARY KEY,
    customer_id INT NOT NULL REFERENCES customers(id),
    coupon_id INT REFERENCES coupons(id),
    status TEXT NOT NULL DEFAULT 'pending',
    subtotal NUMERIC(10, 2) NOT NULL DEFAULT 0,
    discount NUMERIC(10, 2) NOT NULL DEFAULT 0,
    shipping NUMERIC(10, 2) NOT NULL DEFAULT 0,
    total NUMERIC(10, 2) NOT NULL DEFAULT 0,
    shipping_city TEXT,
    shipping_country TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now(),
    shipped_at TIMESTAMPTZ,
    delivered_at TIMESTAMPTZ
);

CREATE TABLE IF NOT EXISTS order_items (
    id SERIAL PRIMARY KEY,
    order_id INT NOT NULL REFERENCES orders(id),
    product_id INT NOT NULL REFERENCES products(id),
    quantity INT NOT NULL,
    unit_price NUMERIC(10, 2) NOT NULL
);

CREATE TABLE IF NOT EXISTS reviews (
    id SERIAL PRIMARY KEY,
    product_id INT NOT NULL REFERENCES products(id),
    customer_id INT NOT NULL REFERENCES customers(id),
    rating INT NOT NULL CHECK (rating BETWEEN 1 AND 5),
    title TEXT,
    body TEXT,
    created_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE IF NOT EXISTS page_views (
    id SERIAL PRIMARY KEY,
    product_id INT NOT NULL REFERENCES products(id),
    customer_id INT REFERENCES customers(id),
    source TEXT NOT NULL DEFAULT 'organic',
    viewed_at TIMESTAMPTZ NOT NULL DEFAULT now()
);

-- ============================================================
-- Seed data: categories
-- ============================================================

INSERT INTO categories (id, name, parent_id) VALUES
    (1, 'Electronics', NULL),
    (2, 'Furniture', NULL),
    (3, 'Stationery', NULL),
    (4, 'Accessories', NULL),
    (5, 'Audio', 1),
    (6, 'Peripherals', 1),
    (7, 'Desks', 2),
    (8, 'Lighting', 2),
    (9, 'Chairs', 2),
    (10, 'Writing', 3),
    (11, 'Paper', 3),
    (12, 'Bags & Cases', 4),
    (13, 'Cables & Adapters', 4)
ON CONFLICT (name) DO NOTHING;

SELECT setval('categories_id_seq', 13);

-- ============================================================
-- Seed data: customers (50)
-- ============================================================

INSERT INTO customers (name, email, phone, city, country, tier, created_at) VALUES
    ('Alice Chen', 'alice@example.com', '+1-555-0101', 'San Francisco', 'US', 'premium', now() - INTERVAL '14 months'),
    ('Bob Martinez', 'bob@example.com', '+1-555-0102', 'Austin', 'US', 'standard', now() - INTERVAL '11 months'),
    ('Carol Wu', 'carol@example.com', '+1-555-0103', 'Seattle', 'US', 'premium', now() - INTERVAL '10 months'),
    ('David Kim', 'david@example.com', '+1-555-0104', 'New York', 'US', 'standard', now() - INTERVAL '9 months'),
    ('Eva Novak', 'eva@example.com', '+44-20-7946-0105', 'London', 'GB', 'premium', now() - INTERVAL '13 months'),
    ('Frank Okafor', 'frank@example.com', '+1-555-0106', 'Chicago', 'US', 'standard', now() - INTERVAL '8 months'),
    ('Grace Tanaka', 'grace@example.com', '+81-3-1234-0107', 'Tokyo', 'JP', 'premium', now() - INTERVAL '12 months'),
    ('Henry Dubois', 'henry@example.com', '+33-1-2345-0108', 'Paris', 'FR', 'standard', now() - INTERVAL '7 months'),
    ('Irene Costa', 'irene@example.com', '+55-11-9876-0109', 'Sao Paulo', 'BR', 'standard', now() - INTERVAL '6 months'),
    ('James O''Brien', 'james@example.com', '+1-555-0110', 'Boston', 'US', 'premium', now() - INTERVAL '11 months'),
    ('Karen Lindgren', 'karen@example.com', '+46-8-123-0111', 'Stockholm', 'SE', 'standard', now() - INTERVAL '5 months'),
    ('Leo Rossi', 'leo@example.com', '+39-06-1234-0112', 'Rome', 'IT', 'standard', now() - INTERVAL '4 months'),
    ('Mia Andersen', 'mia@example.com', '+45-32-123-0113', 'Copenhagen', 'DK', 'premium', now() - INTERVAL '10 months'),
    ('Nathan Patel', 'nathan@example.com', '+91-22-2345-0114', 'Mumbai', 'IN', 'standard', now() - INTERVAL '3 months'),
    ('Olivia Schmidt', 'olivia@example.com', '+49-30-1234-0115', 'Berlin', 'DE', 'premium', now() - INTERVAL '9 months'),
    ('Paul Nguyen', 'paul@example.com', '+1-555-0116', 'Portland', 'US', 'standard', now() - INTERVAL '8 months'),
    ('Quinn Murphy', 'quinn@example.com', '+353-1-234-0117', 'Dublin', 'IE', 'standard', now() - INTERVAL '7 months'),
    ('Rosa Fernandez', 'rosa@example.com', '+34-91-234-0118', 'Madrid', 'ES', 'premium', now() - INTERVAL '6 months'),
    ('Sam Wilson', 'sam@example.com', '+1-555-0119', 'Denver', 'US', 'standard', now() - INTERVAL '5 months'),
    ('Tanya Volkov', 'tanya@example.com', '+7-495-123-0120', 'Moscow', 'RU', 'standard', now() - INTERVAL '4 months'),
    ('Uma Krishnan', 'uma@example.com', '+91-80-2345-0121', 'Bangalore', 'IN', 'premium', now() - INTERVAL '11 months'),
    ('Victor Larsson', 'victor@example.com', '+46-31-123-0122', 'Gothenburg', 'SE', 'standard', now() - INTERVAL '3 months'),
    ('Wendy Zhou', 'wendy@example.com', '+86-21-1234-0123', 'Shanghai', 'CN', 'standard', now() - INTERVAL '2 months'),
    ('Xavier Dumont', 'xavier@example.com', '+33-4-5678-0124', 'Lyon', 'FR', 'premium', now() - INTERVAL '8 months'),
    ('Yuki Sato', 'yuki@example.com', '+81-6-1234-0125', 'Osaka', 'JP', 'standard', now() - INTERVAL '7 months'),
    ('Zara Ahmed', 'zara@example.com', '+92-21-234-0126', 'Karachi', 'PK', 'standard', now() - INTERVAL '1 month'),
    ('Aaron Brooks', 'aaron@example.com', '+1-555-0127', 'Miami', 'US', 'standard', now() - INTERVAL '6 months'),
    ('Bianca Moretti', 'bianca@example.com', '+39-02-1234-0128', 'Milan', 'IT', 'premium', now() - INTERVAL '10 months'),
    ('Carlos Reyes', 'carlos@example.com', '+52-55-1234-0129', 'Mexico City', 'MX', 'standard', now() - INTERVAL '5 months'),
    ('Diana Petrova', 'diana@example.com', '+359-2-123-0130', 'Sofia', 'BG', 'standard', now() - INTERVAL '4 months'),
    ('Elias Hoffman', 'elias@example.com', '+49-89-1234-0131', 'Munich', 'DE', 'premium', now() - INTERVAL '9 months'),
    ('Fatima Al-Rashid', 'fatima@example.com', '+971-4-234-0132', 'Dubai', 'AE', 'premium', now() - INTERVAL '8 months'),
    ('George Papadopoulos', 'george@example.com', '+30-21-1234-0133', 'Athens', 'GR', 'standard', now() - INTERVAL '3 months'),
    ('Hannah Berg', 'hannah@example.com', '+47-22-123-0134', 'Oslo', 'NO', 'standard', now() - INTERVAL '7 months'),
    ('Ivan Kowalski', 'ivan@example.com', '+48-22-123-0135', 'Warsaw', 'PL', 'standard', now() - INTERVAL '6 months'),
    ('Julia Santos', 'julia@example.com', '+55-21-9876-0136', 'Rio de Janeiro', 'BR', 'premium', now() - INTERVAL '5 months'),
    ('Kevin O''Reilly', 'kevin@example.com', '+353-1-567-0137', 'Cork', 'IE', 'standard', now() - INTERVAL '2 months'),
    ('Lina Johansson', 'lina@example.com', '+46-40-123-0138', 'Malmo', 'SE', 'standard', now() - INTERVAL '4 months'),
    ('Marco Bianchi', 'marco@example.com', '+39-055-123-0139', 'Florence', 'IT', 'standard', now() - INTERVAL '3 months'),
    ('Nina Takahashi', 'nina@example.com', '+81-75-123-0140', 'Kyoto', 'JP', 'premium', now() - INTERVAL '9 months'),
    ('Oscar Mendez', 'oscar@example.com', '+54-11-1234-0141', 'Buenos Aires', 'AR', 'standard', now() - INTERVAL '2 months'),
    ('Petra Novotna', 'petra@example.com', '+420-2-1234-0142', 'Prague', 'CZ', 'standard', now() - INTERVAL '6 months'),
    ('Raj Sharma', 'raj@example.com', '+91-11-2345-0143', 'Delhi', 'IN', 'premium', now() - INTERVAL '8 months'),
    ('Sophie Martin', 'sophie@example.com', '+33-5-6789-0144', 'Bordeaux', 'FR', 'standard', now() - INTERVAL '5 months'),
    ('Tom Henderson', 'tom@example.com', '+44-131-234-0145', 'Edinburgh', 'GB', 'standard', now() - INTERVAL '7 months'),
    ('Ursula Weber', 'ursula@example.com', '+49-40-1234-0146', 'Hamburg', 'DE', 'standard', now() - INTERVAL '4 months'),
    ('Vincent Lam', 'vincent@example.com', '+852-2345-0147', 'Hong Kong', 'HK', 'premium', now() - INTERVAL '10 months'),
    ('Wanda Kowalczyk', 'wanda@example.com', '+48-12-123-0148', 'Krakow', 'PL', 'standard', now() - INTERVAL '3 months'),
    ('Xander Vos', 'xander@example.com', '+31-20-123-0149', 'Amsterdam', 'NL', 'premium', now() - INTERVAL '11 months'),
    ('Yolanda Cruz', 'yolanda@example.com', '+34-93-234-0150', 'Barcelona', 'ES', 'standard', now() - INTERVAL '2 months')
ON CONFLICT (email) DO NOTHING;

-- ============================================================
-- Seed data: products (40)
-- ============================================================

INSERT INTO products (sku, name, category_id, price, cost, weight_kg, is_active) VALUES
    -- Electronics > Peripherals
    ('ELEC-KB-001', 'Wireless Keyboard', 6, 79.99, 32.00, 0.68, true),
    ('ELEC-KB-002', 'Mechanical Keyboard RGB', 6, 149.99, 60.00, 0.95, true),
    ('ELEC-MS-001', 'Ergonomic Mouse', 6, 89.99, 35.00, 0.12, true),
    ('ELEC-MS-002', 'Gaming Mouse Pro', 6, 69.99, 28.00, 0.09, true),
    ('ELEC-HB-001', 'USB-C Hub 7-Port', 6, 49.99, 18.00, 0.15, true),
    ('ELEC-HB-002', 'Thunderbolt Dock', 6, 249.99, 95.00, 0.45, true),
    ('ELEC-WC-001', 'Webcam HD 1080p', 6, 109.99, 42.00, 0.18, true),
    ('ELEC-WC-002', 'Webcam 4K Pro', 6, 199.99, 78.00, 0.22, true),
    -- Electronics > Audio
    ('ELEC-HP-001', 'Noise-Cancelling Headphones', 5, 299.99, 120.00, 0.25, true),
    ('ELEC-HP-002', 'Wireless Earbuds', 5, 129.99, 48.00, 0.05, true),
    ('ELEC-SP-001', 'Desktop Speakers', 5, 179.99, 72.00, 2.10, true),
    ('ELEC-MC-001', 'USB Condenser Microphone', 5, 139.99, 55.00, 0.38, true),
    -- Furniture > Desks
    ('FURN-DK-001', 'Standing Desk 60"', 7, 599.00, 240.00, 32.00, true),
    ('FURN-DK-002', 'Standing Desk 48"', 7, 499.00, 200.00, 26.00, true),
    ('FURN-DK-003', 'Corner Desk L-Shape', 7, 449.00, 180.00, 28.00, true),
    -- Furniture > Chairs
    ('FURN-CH-001', 'Ergonomic Task Chair', 9, 799.00, 320.00, 18.50, true),
    ('FURN-CH-002', 'Mesh Office Chair', 9, 349.00, 140.00, 14.00, true),
    ('FURN-CH-003', 'Standing Desk Stool', 9, 199.00, 80.00, 6.00, true),
    -- Furniture > Lighting
    ('FURN-LT-001', 'LED Desk Lamp', 8, 64.99, 26.00, 1.20, true),
    ('FURN-LT-002', 'Monitor Light Bar', 8, 89.99, 36.00, 0.55, true),
    ('FURN-LT-003', 'Floor Standing Lamp', 8, 159.99, 64.00, 4.50, true),
    -- Furniture (misc)
    ('FURN-MA-001', 'Dual Monitor Arm', 2, 129.99, 52.00, 3.80, true),
    ('FURN-MA-002', 'Single Monitor Arm', 2, 79.99, 32.00, 2.20, true),
    ('FURN-FP-001', 'Under-Desk Footrest', 2, 44.99, 18.00, 1.60, true),
    -- Stationery > Writing
    ('STAT-MP-001', 'Mechanical Pencil Set', 10, 24.99, 8.00, 0.12, true),
    ('STAT-FP-001', 'Fountain Pen', 10, 89.99, 36.00, 0.04, true),
    ('STAT-MK-001', 'Whiteboard Marker 12-Pack', 10, 14.99, 4.50, 0.30, true),
    -- Stationery > Paper
    ('STAT-NB-001', 'Notebook 3-Pack', 11, 18.50, 5.50, 0.60, true),
    ('STAT-NB-002', 'Premium Dot-Grid Journal', 11, 29.99, 12.00, 0.35, true),
    ('STAT-SK-001', 'Sticky Notes Mega Pack', 11, 12.99, 3.80, 0.45, true),
    -- Accessories > Bags & Cases
    ('ACCS-BG-001', 'Laptop Backpack', 12, 89.99, 36.00, 0.90, true),
    ('ACCS-BG-002', 'Laptop Sleeve 15"', 12, 34.99, 12.00, 0.25, true),
    ('ACCS-BG-003', 'Tech Organizer Pouch', 12, 24.99, 8.00, 0.18, true),
    -- Accessories > Cables & Adapters
    ('ACCS-CB-001', 'USB-C Cable 2m', 13, 14.99, 3.50, 0.08, true),
    ('ACCS-CB-002', 'HDMI 2.1 Cable 3m', 13, 19.99, 5.00, 0.15, true),
    ('ACCS-CB-003', 'Cable Management Kit', 13, 19.99, 6.00, 0.30, true),
    ('ACCS-AD-001', 'USB-C to DisplayPort', 13, 29.99, 10.00, 0.05, true),
    -- Discontinued product
    ('ELEC-KB-OLD', 'Classic Wired Keyboard', 6, 39.99, 16.00, 0.75, false),
    -- Accessories (misc)
    ('ACCS-WR-001', 'Desk Mat XXL', 4, 34.99, 12.00, 0.55, true),
    ('ACCS-WR-002', 'Wrist Rest Keyboard', 4, 24.99, 8.00, 0.30, true)
ON CONFLICT (sku) DO NOTHING;

-- ============================================================
-- Seed data: inventory
-- ============================================================

INSERT INTO inventory (product_id, quantity, reorder_threshold, warehouse, last_restocked_at)
SELECT p.id,
    CASE
        WHEN p.sku IN ('ELEC-MS-001', 'FURN-DK-001', 'FURN-CH-001') THEN (random() * 5)::int        -- low stock
        WHEN p.sku LIKE 'STAT-%' THEN 150 + (random() * 200)::int                                     -- stationery: high stock
        WHEN p.sku LIKE 'ACCS-CB-%' THEN 200 + (random() * 300)::int                                  -- cables: very high stock
        ELSE 15 + (random() * 80)::int                                                                 -- everything else
    END,
    CASE
        WHEN p.price > 400 THEN 5
        WHEN p.price > 100 THEN 10
        ELSE 20
    END,
    CASE WHEN random() < 0.3 THEN 'us-west' ELSE 'us-east' END,
    now() - (random() * INTERVAL '60 days')
FROM products p
WHERE p.is_active = true
ON CONFLICT (product_id) DO NOTHING;

-- ============================================================
-- Seed data: coupons
-- ============================================================

INSERT INTO coupons (code, discount_pct, min_order, max_uses, times_used, valid_from, valid_until, is_active) VALUES
    ('WELCOME10', 10.00, 50.00, NULL, 87, now() - INTERVAL '6 months', now() + INTERVAL '6 months', true),
    ('SUMMER20', 20.00, 100.00, 500, 342, now() - INTERVAL '2 months', now() + INTERVAL '1 month', true),
    ('VIP15', 15.00, 0.00, NULL, 156, now() - INTERVAL '12 months', now() + INTERVAL '12 months', true),
    ('FLASH30', 30.00, 200.00, 100, 100, now() - INTERVAL '1 month', now() - INTERVAL '1 day', false),
    ('FREESHIP', 0.00, 75.00, NULL, 412, now() - INTERVAL '3 months', now() + INTERVAL '3 months', true),
    ('NEWYEAR25', 25.00, 150.00, 200, 63, now() - INTERVAL '3 months', now() - INTERVAL '2 months', false),
    ('BULK10', 10.00, 500.00, NULL, 28, now() - INTERVAL '1 month', now() + INTERVAL '5 months', true),
    ('STUDENT15', 15.00, 30.00, NULL, 203, now() - INTERVAL '8 months', now() + INTERVAL '4 months', true)
ON CONFLICT (code) DO NOTHING;

-- ============================================================
-- Seed data: orders (~200) and order_items (~500)
-- Uses generate_series + random assignment for volume.
-- ============================================================

-- Generate 200 orders spread over the last 90 days
INSERT INTO orders (customer_id, coupon_id, status, subtotal, discount, shipping, total, shipping_city, shipping_country, created_at, shipped_at, delivered_at)
SELECT
    -- random customer (1-50)
    1 + (random() * 49)::int,
    -- 20% chance of coupon
    CASE WHEN random() < 0.2 THEN 1 + (random() * 7)::int ELSE NULL END,
    -- status weighted toward completed
    CASE
        WHEN age < INTERVAL '2 days' THEN 'pending'
        WHEN age < INTERVAL '5 days' THEN (ARRAY['pending', 'shipped', 'shipped'])[1 + (random() * 2)::int]
        ELSE (ARRAY['completed', 'completed', 'completed', 'completed', 'cancelled'])[1 + (random() * 4)::int]
    END,
    -- subtotal placeholder (updated below)
    0, 0, 0, 0,
    -- shipping address (same as customer for simplicity)
    NULL, NULL,
    -- created_at spread over 90 days
    now() - age,
    -- shipped_at
    CASE WHEN age > INTERVAL '3 days' THEN now() - age + INTERVAL '1 day' + (random() * INTERVAL '2 days') ELSE NULL END,
    -- delivered_at
    CASE WHEN age > INTERVAL '7 days' THEN now() - age + INTERVAL '4 days' + (random() * INTERVAL '3 days') ELSE NULL END
FROM (
    SELECT (random() * 90)::int * INTERVAL '1 day' + (random() * INTERVAL '23 hours') AS age
    FROM generate_series(1, 200)
) AS ages;

-- Drop the dummy column (shipped_at/delivered_at for pending/cancelled)
UPDATE orders SET
    shipped_at = NULL WHERE status IN ('pending', 'cancelled');
UPDATE orders SET
    delivered_at = NULL WHERE status IN ('pending', 'shipped', 'cancelled');

-- Fill shipping address from customer
UPDATE orders o SET
    shipping_city = c.city,
    shipping_country = c.country
FROM customers c
WHERE o.customer_id = c.id;

-- Generate 1-4 order items per order
INSERT INTO order_items (order_id, product_id, quantity, unit_price)
SELECT
    o.id,
    p.id,
    1 + (random() * 2)::int,
    p.price
FROM orders o
CROSS JOIN LATERAL (
    SELECT id, price
    FROM products
    WHERE is_active = true
    ORDER BY random()
    LIMIT 1 + (random() * 3)::int
) p;

-- Update order totals from items
UPDATE orders o SET
    subtotal = item_totals.s,
    shipping = CASE WHEN item_totals.s >= 75 THEN 0 ELSE 9.99 END,
    discount = CASE
        WHEN o.coupon_id IS NOT NULL THEN ROUND(item_totals.s * (
            SELECT c.discount_pct / 100 FROM coupons c WHERE c.id = o.coupon_id
        ), 2)
        ELSE 0
    END
FROM (
    SELECT order_id, SUM(quantity * unit_price) AS s
    FROM order_items
    GROUP BY order_id
) item_totals
WHERE o.id = item_totals.order_id;

UPDATE orders SET total = subtotal - discount + shipping;

-- ============================================================
-- Seed data: reviews (~120)
-- Only for completed orders, 60% review rate
-- ============================================================

INSERT INTO reviews (product_id, customer_id, rating, title, body, created_at)
SELECT
    oi.product_id,
    o.customer_id,
    ratings.r,
    titles.t,
    bodies.b,
    o.delivered_at + (random() * INTERVAL '14 days')
FROM orders o
JOIN order_items oi ON oi.order_id = o.id
CROSS JOIN LATERAL (SELECT 1 + (random() * 4)::int AS r) ratings
CROSS JOIN LATERAL (
    SELECT (ARRAY[
        'Great product!', 'Exactly what I needed', 'Good value', 'Solid build quality',
        'Better than expected', 'Decent for the price', 'Would buy again',
        'Not bad', 'Could be better', 'Disappointed', 'Perfect for my setup',
        'Excellent quality', 'Works as advertised', 'Highly recommend'
    ])[1 + (random() * 13)::int] AS t
) titles
CROSS JOIN LATERAL (
    SELECT (ARRAY[
        'Been using this for a few weeks now and it''s holding up great. Exactly as described.',
        'Fast shipping, good packaging. The product itself is solid quality.',
        'Works perfectly for my home office setup. No complaints.',
        'A bit pricey but the quality justifies it. Would recommend to others.',
        'Does what it says on the tin. Nothing fancy but reliable.',
        'Pleasantly surprised by the build quality at this price point.',
        'Had some initial setup issues but works fine now. Customer service was helpful.',
        'Upgraded from a cheaper alternative and the difference is night and day.',
        'Good product overall. Dock one star for the packaging which was a bit flimsy.',
        'Perfect addition to my workspace. Gets daily use and still looks new.',
        'Bought this as a gift and they loved it. Clean design and well made.',
        'Third time ordering from this store. Consistent quality every time.'
    ])[1 + (random() * 11)::int] AS b
) bodies
WHERE o.status = 'completed'
AND o.delivered_at IS NOT NULL
AND random() < 0.6;

-- ============================================================
-- Seed data: page_views (~1000)
-- Browsing activity over the last 30 days
-- ============================================================

INSERT INTO page_views (product_id, customer_id, source, viewed_at)
SELECT
    -- popular products get more views
    p.id,
    -- 70% logged-in, 30% anonymous
    CASE WHEN random() < 0.7 THEN 1 + (random() * 49)::int ELSE NULL END,
    -- traffic sources
    (ARRAY['organic', 'organic', 'organic', 'google', 'google', 'email', 'social', 'direct', 'referral'])[1 + (random() * 8)::int],
    now() - (random() * INTERVAL '30 days')
FROM generate_series(1, 1000)
CROSS JOIN LATERAL (
    SELECT id FROM products WHERE is_active = true ORDER BY random() LIMIT 1
) p;

-- ============================================================
-- Indexes for common queries
-- ============================================================

CREATE INDEX IF NOT EXISTS idx_orders_customer_id ON orders(customer_id);
CREATE INDEX IF NOT EXISTS idx_orders_created_at ON orders(created_at);
CREATE INDEX IF NOT EXISTS idx_orders_status ON orders(status);
CREATE INDEX IF NOT EXISTS idx_order_items_order_id ON order_items(order_id);
CREATE INDEX IF NOT EXISTS idx_order_items_product_id ON order_items(product_id);
CREATE INDEX IF NOT EXISTS idx_reviews_product_id ON reviews(product_id);
CREATE INDEX IF NOT EXISTS idx_reviews_customer_id ON reviews(customer_id);
CREATE INDEX IF NOT EXISTS idx_page_views_product_id ON page_views(product_id);
CREATE INDEX IF NOT EXISTS idx_page_views_viewed_at ON page_views(viewed_at);
CREATE INDEX IF NOT EXISTS idx_products_category_id ON products(category_id);

COMMIT;
