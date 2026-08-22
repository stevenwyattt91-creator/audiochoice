#if POSTGRES
using System.Text.Json;
using AudioChoice.Api.Contracts;
using Npgsql;
using NpgsqlTypes;

namespace AudioChoice.Api.Services;

public sealed class PostgresUserDataStore(NpgsqlDataSource dataSource) : IUserDataStore
{
    private sealed record ProfileSettings(
        IReadOnlyList<FilterRule> Rules,
        IReadOnlyList<string> CustomWords);

    public IReadOnlyList<FilterProfile> ListProfiles(Guid userID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select id, name, is_active, settings_json::text, created_at, updated_at
            from filter_profiles where user_id = $1
            order by is_active desc, name;
            """, connection);
        command.Parameters.AddWithValue(userID);
        using var reader = command.ExecuteReader();
        var values = new List<FilterProfile>();
        while (reader.Read()) values.Add(ReadProfile(reader));
        return values;
    }

    public FilterProfile? SaveProfile(
        Guid userID, Guid? profileID, FilterProfileUpsertRequest request)
    {
        var name = Clean(request.Name, 100);
        if (name is null || request.Rules.Count > 100 || request.CustomWords.Count > 500) return null;
        var rules = request.Rules.Select(CleanRule).Where(x => x is not null).Cast<FilterRule>().ToArray();
        var words = request.CustomWords.Select(x => Clean(x, 100)).Where(x => x is not null)
            .Cast<string>().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var settings = JsonSerializer.Serialize(new ProfileSettings(rules, words));
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        if (request.IsActive)
        {
            using var deactivate = new NpgsqlCommand(
                "update filter_profiles set is_active = false, updated_at = now() where user_id = $1 and is_active;",
                connection, transaction);
            deactivate.Parameters.AddWithValue(userID);
            deactivate.ExecuteNonQuery();
        }
        var id = profileID ?? Guid.NewGuid();
        using var command = new NpgsqlCommand(profileID is null ? """
            insert into filter_profiles(id, user_id, name, is_active, settings_json, created_at, updated_at)
            values ($1, $2, $3, $4, $5, now(), now())
            returning id, name, is_active, settings_json::text, created_at, updated_at;
            """ : """
            update filter_profiles set name = $3, is_active = $4, settings_json = $5, updated_at = now()
            where id = $1 and user_id = $2
            returning id, name, is_active, settings_json::text, created_at, updated_at;
            """, connection, transaction);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(name);
        command.Parameters.AddWithValue(request.IsActive);
        command.Parameters.AddWithValue(NpgsqlDbType.Jsonb, settings);
        using var reader = command.ExecuteReader();
        var value = reader.Read() ? ReadProfile(reader) : null;
        reader.Close();
        transaction.Commit();
        return value;
    }

    public bool DeleteProfile(Guid userID, Guid profileID) => DeleteOwned(
        "filter_profiles", userID, profileID);

    public IReadOnlyList<BookNote> ListNotes(Guid userID, Guid? libraryBookID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select id, library_book_id, position_seconds, note_text, created_at, updated_at
            from book_notes
            where user_id = $1 and ($2::uuid is null or library_book_id = $2)
            order by updated_at desc;
            """, connection);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(NpgsqlDbType.Uuid, (object?)libraryBookID ?? DBNull.Value);
        using var reader = command.ExecuteReader();
        var values = new List<BookNote>();
        while (reader.Read()) values.Add(ReadNote(reader));
        return values;
    }

    public BookNote? SaveNote(Guid userID, Guid? noteID, BookNoteUpsertRequest request)
    {
        var text = Clean(request.Text, 10_000);
        if (text is null || request.PositionSeconds is double p && (!double.IsFinite(p) || p < 0)) return null;
        using var connection = dataSource.OpenConnection();
        if (!OwnsBook(connection, userID, request.LibraryBookID)) return null;
        var id = noteID ?? Guid.NewGuid();
        using var command = new NpgsqlCommand(noteID is null ? """
            insert into book_notes(id, user_id, library_book_id, position_seconds, note_text, created_at, updated_at)
            values ($1, $2, $3, $4, $5, now(), now())
            returning id, library_book_id, position_seconds, note_text, created_at, updated_at;
            """ : """
            update book_notes set library_book_id = $3, position_seconds = $4, note_text = $5, updated_at = now()
            where id = $1 and user_id = $2
            returning id, library_book_id, position_seconds, note_text, created_at, updated_at;
            """, connection);
        command.Parameters.AddWithValue(id);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(request.LibraryBookID);
        command.Parameters.AddWithValue(NpgsqlDbType.Double, (object?)request.PositionSeconds ?? DBNull.Value);
        command.Parameters.AddWithValue(text);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadNote(reader) : null;
    }

    public bool DeleteNote(Guid userID, Guid noteID) => DeleteOwned("book_notes", userID, noteID);

    public IReadOnlyList<LibraryCollection> ListCollections(Guid userID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            select c.id, c.name, c.created_at, c.updated_at, b.library_book_id
            from library_collections c
            left join library_collection_books b on b.collection_id = c.id
            where c.user_id = $1
            order by c.name, b.added_at;
            """, connection);
        command.Parameters.AddWithValue(userID);
        using var reader = command.ExecuteReader();
        var values = new Dictionary<Guid, (string Name, DateTimeOffset Created, DateTimeOffset Updated, List<Guid> Books)>();
        while (reader.Read())
        {
            var id = reader.GetGuid(0);
            if (!values.TryGetValue(id, out var value))
                value = (reader.GetString(1), reader.GetFieldValue<DateTimeOffset>(2), reader.GetFieldValue<DateTimeOffset>(3), []);
            if (!reader.IsDBNull(4)) value.Books.Add(reader.GetGuid(4));
            values[id] = value;
        }
        return values.Select(x => new LibraryCollection(
            x.Key, x.Value.Name, x.Value.Books, x.Value.Created, x.Value.Updated)).ToArray();
    }

    public LibraryCollection? SaveCollection(
        Guid userID, Guid? collectionID, CollectionUpsertRequest request)
    {
        var name = Clean(request.Name, 100);
        var books = request.LibraryBookIDs.Distinct().ToArray();
        if (name is null || books.Length > 1_000) return null;
        using var connection = dataSource.OpenConnection();
        if (books.Any(book => !OwnsBook(connection, userID, book))) return null;
        using var transaction = connection.BeginTransaction();
        var id = collectionID ?? Guid.NewGuid();
        using (var command = new NpgsqlCommand(collectionID is null ? """
            insert into library_collections(id, user_id, name, created_at, updated_at)
            values ($1, $2, $3, now(), now());
            """ : """
            update library_collections set name = $3, updated_at = now()
            where id = $1 and user_id = $2;
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(userID); command.Parameters.AddWithValue(name);
            if (command.ExecuteNonQuery() == 0) return null;
        }
        using (var clear = new NpgsqlCommand(
            "delete from library_collection_books where collection_id = $1;", connection, transaction))
        { clear.Parameters.AddWithValue(id); clear.ExecuteNonQuery(); }
        foreach (var book in books)
        {
            using var add = new NpgsqlCommand(
                "insert into library_collection_books(collection_id, library_book_id, added_at) values ($1, $2, now());",
                connection, transaction);
            add.Parameters.AddWithValue(id); add.Parameters.AddWithValue(book); add.ExecuteNonQuery();
        }
        transaction.Commit();
        return ListCollections(userID).Single(x => x.ID == id);
    }

    public bool DeleteCollection(Guid userID, Guid collectionID) =>
        DeleteOwned("library_collections", userID, collectionID);

    public BookFilterSettings? GetBookFilterSettings(Guid userID, Guid libraryBookID)
    {
        using var connection = dataSource.OpenConnection();
        if (!OwnsBook(connection, userID, libraryBookID)) return null;
        using var command = new NpgsqlCommand("""
            select disabled_category_ids, disabled_group_ids, disabled_event_keys,
                   disabled_aggregate_keys, updated_at
            from book_filter_settings where user_id = $1 and library_book_id = $2;
            """, connection);
        command.Parameters.AddWithValue(userID); command.Parameters.AddWithValue(libraryBookID);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBookFilterSettings(reader, libraryBookID)
            : new BookFilterSettings(libraryBookID, [], [], [], [], DateTimeOffset.UnixEpoch);
    }

    public BookFilterSettings? SaveBookFilterSettings(
        Guid userID, Guid libraryBookID, BookFilterSettingsUpsertRequest request)
    {
        if (!ValidSettings(request)) return null;
        using var connection = dataSource.OpenConnection();
        if (!OwnsBook(connection, userID, libraryBookID)) return null;
        using var command = new NpgsqlCommand("""
            insert into book_filter_settings(
                user_id, library_book_id, disabled_category_ids, disabled_group_ids,
                disabled_event_keys, disabled_aggregate_keys, updated_at)
            values ($1, $2, $3, $4, $5, $6, now())
            on conflict (user_id, library_book_id) do update set
                disabled_category_ids = excluded.disabled_category_ids,
                disabled_group_ids = excluded.disabled_group_ids,
                disabled_event_keys = excluded.disabled_event_keys,
                disabled_aggregate_keys = excluded.disabled_aggregate_keys,
                updated_at = now()
            returning disabled_category_ids, disabled_group_ids, disabled_event_keys,
                      disabled_aggregate_keys, updated_at;
            """, connection);
        command.Parameters.AddWithValue(userID); command.Parameters.AddWithValue(libraryBookID);
        command.Parameters.AddWithValue(request.DisabledCategoryIDs.Distinct().ToArray());
        command.Parameters.AddWithValue(request.DisabledGroupIDs.Distinct().ToArray());
        command.Parameters.AddWithValue(CleanKeys(request.DisabledEventKeys));
        command.Parameters.AddWithValue(CleanKeys(request.DisabledAggregateKeys));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBookFilterSettings(reader, libraryBookID) : null;
    }

    private bool DeleteOwned(string table, Guid userID, Guid id)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            $"delete from {table} where id = $1 and user_id = $2;", connection);
        command.Parameters.AddWithValue(id); command.Parameters.AddWithValue(userID);
        return command.ExecuteNonQuery() > 0;
    }
    private static bool OwnsBook(NpgsqlConnection connection, Guid userID, Guid bookID)
    {
        using var command = new NpgsqlCommand(
            "select exists(select 1 from user_library_books where id = $1 and user_id = $2);", connection);
        command.Parameters.AddWithValue(bookID); command.Parameters.AddWithValue(userID);
        return (bool)(command.ExecuteScalar() ?? false);
    }
    private static FilterProfile ReadProfile(NpgsqlDataReader reader)
    {
        var settings = JsonSerializer.Deserialize<ProfileSettings>(reader.GetString(3))
            ?? new ProfileSettings([], []);
        return new(reader.GetGuid(0), reader.GetString(1), reader.GetBoolean(2),
            settings.Rules, settings.CustomWords, reader.GetFieldValue<DateTimeOffset>(4),
            reader.GetFieldValue<DateTimeOffset>(5));
    }
    private static BookNote ReadNote(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.IsDBNull(2) ? null : reader.GetDouble(2),
        reader.GetString(3), reader.GetFieldValue<DateTimeOffset>(4), reader.GetFieldValue<DateTimeOffset>(5));
    private static FilterRule? CleanRule(FilterRule value)
    {
        var key = Clean(value.Key, 100); var action = Clean(value.Action, 30); var severity = Clean(value.Severity, 30);
        return key is null || action is null || severity is null ? null : new(key, value.Enabled, action, severity);
    }
    private static string? Clean(string? value, int maximum) => string.IsNullOrWhiteSpace(value)
        ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
    private static BookFilterSettings ReadBookFilterSettings(NpgsqlDataReader reader, Guid libraryBookID) => new(
        libraryBookID, reader.GetFieldValue<Guid[]>(0), reader.GetFieldValue<Guid[]>(1),
        reader.GetFieldValue<string[]>(2), reader.GetFieldValue<string[]>(3),
        reader.GetFieldValue<DateTimeOffset>(4));
    private static bool ValidSettings(BookFilterSettingsUpsertRequest value) =>
        value.DisabledCategoryIDs.Count <= 100 && value.DisabledGroupIDs.Count <= 200 &&
        value.DisabledEventKeys.Count <= 20_000 && value.DisabledAggregateKeys.Count <= 2_000;
    private static string[] CleanKeys(IEnumerable<string> values) => values
        .Select(x => Clean(x, 100)).Where(x => x is not null).Cast<string>()
        .Distinct(StringComparer.Ordinal).ToArray();
}
#endif
