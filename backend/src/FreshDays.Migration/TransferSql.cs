namespace FreshDays.Migration;

internal static partial class SqliteTransfer
{
    private const string StageSource = """
        CREATE TEMP TABLE source_lists ON COMMIT DROP AS
        SELECT id, name, CASE WHEN data IS JSON ARRAY WITH UNIQUE KEYS AND pg_input_is_valid(data, 'jsonb')
                              THEN data::jsonb END AS data
        FROM jsonb_to_recordset(@lists::jsonb) AS rows(id bigint, name text, data text);
        CREATE TEMP TABLE source_items ON COMMIT DROP AS
        SELECT source.id, source.name AS list, item, position::int
        FROM source_lists AS source CROSS JOIN LATERAL jsonb_array_elements(data) WITH ORDINALITY AS items(item, position);
        CREATE TEMP TABLE source_subscribers ON COMMIT DROP AS
        SELECT * FROM jsonb_to_recordset(@subscribers::jsonb) AS rows(id bigint, user_id text);
        CREATE TEMP TABLE source_notifications ON COMMIT DROP AS
        SELECT * FROM jsonb_to_recordset(@notifications::jsonb)
            AS rows(id bigint, day text, user_id text, retry_key text, sent bigint);
        """;

    private const string ValidateSource = """
        SELECT coalesce(string_agg(message, chr(10) ORDER BY message), '')
        FROM (
            SELECT format('lists rowid %s (%s): unknown or duplicate list name', id, name) AS message
            FROM (SELECT *, count(*) OVER (PARTITION BY name) AS copies FROM source_lists) AS lists
            WHERE name IS NULL OR name NOT IN ('inventory', 'shopping') OR copies > 1
            UNION ALL
            SELECT format('lists rowid %s (%s): invalid JSON array or duplicate JSON keys', id, name)
            FROM source_lists WHERE data IS NULL
            UNION ALL
            SELECT format('lists rowid %s (%s), item %s (id %s): invalid item or duplicate ID',
                          id, list, position, left(item->>'id', 80))
            FROM (
                SELECT *, count(*) OVER (PARTITION BY list, item->>'id') AS copies FROM source_items
            ) AS items
            WHERE jsonb_typeof(item) <> 'object'
               OR jsonb_typeof(item->'id') IS DISTINCT FROM 'string'
               OR item->>'id' !~ '[^[:space:]]'
               OR jsonb_typeof(item->'name') IS DISTINCT FROM 'string'
               OR item->>'name' !~ '[^[:space:]]'
               OR char_length(item->>'name') > 100
               OR copies > 1
               OR (list = 'inventory' AND item->>'expiresAt' IS NOT NULL AND (
                   jsonb_typeof(item->'expiresAt') <> 'string'
                   OR item->>'expiresAt' !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$'
                   OR NOT pg_input_is_valid(item->>'expiresAt', 'date')))
            UNION ALL
            SELECT format('subscribers rowid %s: invalid or duplicate user ID', id)
            FROM (SELECT *, count(*) OVER (PARTITION BY user_id) AS copies FROM source_subscribers) AS subscribers
            WHERE user_id IS NULL OR user_id !~ '[^[:space:]]' OR copies > 1
            UNION ALL
            SELECT format('notifications rowid %s (day %s): invalid or duplicate notification', id, day)
            FROM (
                SELECT *, count(*) OVER (PARTITION BY day, user_id) AS copies FROM source_notifications
            ) AS notifications
            WHERE day IS NULL OR day !~ '^[0-9]{4}-[0-9]{2}-[0-9]{2}$' OR NOT pg_input_is_valid(day, 'date')
               OR user_id IS NULL OR user_id !~ '[^[:space:]]'
               OR retry_key IS NULL OR retry_key !~ '^[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}$'
               OR sent IS NULL OR sent NOT IN (0, 1)
               OR copies > 1
        ) AS invalid;
        """;

    private const string TransferRows = """
        LOCK TABLE inventory_items, shopping_items, line_subscriptions, line_notifications IN SHARE ROW EXCLUSIVE MODE;

        INSERT INTO inventory_items (household_id, id, name, position, expires_at, extra)
        SELECT @household, item->>'id', item->>'name', position, item->'expiresAt', item - 'id' - 'name' - 'expiresAt'
        FROM source_items WHERE list = 'inventory'
        ON CONFLICT (household_id, id) DO NOTHING;

        INSERT INTO shopping_items (household_id, id, name, position, completed, extra)
        SELECT @household, item->>'id', item->>'name', position, item->'completed', item - 'id' - 'name' - 'completed'
        FROM source_items WHERE list = 'shopping'
        ON CONFLICT (household_id, id) DO NOTHING;

        INSERT INTO line_subscriptions (household_id, user_id)
        SELECT @household, user_id FROM source_subscribers
        ON CONFLICT (household_id, user_id) DO NOTHING;

        INSERT INTO line_notifications (household_id, day, user_id, retry_key, sent)
        SELECT @household, day::date, user_id, retry_key::uuid, sent = 1 FROM source_notifications
        ON CONFLICT (household_id, day, user_id) DO NOTHING;
        """;

    private const string VerifyRows = """
        WITH compared AS (
            SELECT 'inventory' AS name,
                ARRAY(SELECT item FROM source_items WHERE list = 'inventory' ORDER BY position) AS source,
                ARRAY(SELECT extra || jsonb_build_object('id', id, 'name', name) ||
                             CASE WHEN expires_at IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('expiresAt', expires_at) END
                      FROM inventory_items WHERE household_id = @household ORDER BY position, id COLLATE "C") AS destination
            UNION ALL
            SELECT 'shopping',
                ARRAY(SELECT item FROM source_items WHERE list = 'shopping' ORDER BY position),
                ARRAY(SELECT extra || jsonb_build_object('id', id, 'name', name) ||
                             CASE WHEN completed IS NULL THEN '{}'::jsonb ELSE jsonb_build_object('completed', completed) END
                      FROM shopping_items WHERE household_id = @household ORDER BY position, id COLLATE "C")
            UNION ALL
            SELECT 'subscribers',
                ARRAY(SELECT to_jsonb(user_id) FROM source_subscribers ORDER BY user_id COLLATE "C"),
                ARRAY(SELECT to_jsonb(user_id) FROM line_subscriptions WHERE household_id = @household ORDER BY user_id COLLATE "C")
            UNION ALL
            SELECT 'notifications',
                ARRAY(SELECT jsonb_build_array(day::date, user_id, retry_key::uuid, sent = 1)
                      FROM source_notifications ORDER BY day::date, user_id COLLATE "C"),
                ARRAY(SELECT jsonb_build_array(day, user_id, retry_key, sent)
                      FROM line_notifications WHERE household_id = @household ORDER BY day, user_id COLLATE "C")
        )
        SELECT bool_and(source = destination),
               jsonb_build_object('dryRun', @dry_run::boolean, 'householdId', @household::uuid,
                   'groups', jsonb_object_agg(name, jsonb_build_object(
                       'source', cardinality(source), 'destination', cardinality(destination),
                       'contentMatches', source = destination)))::text
        FROM compared;
        """;
}
