#if POSTGRES
using AudioChoice.Api.Contracts;
using Npgsql;

namespace AudioChoice.Api.Services;

public sealed class PostgresUserLibraryStore(NpgsqlDataSource dataSource) : IUserLibraryStore
{
    private const string BookSelect = """
        select lb.id,
               e.fingerprint_version, e.sha256, e.file_size, e.duration_seconds,
               e.file_type, e.work_title, e.author, e.series_title, e.series_number,
               e.edition_type, e.part_number, e.total_parts,
               lb.title, lb.author, lb.narrator,
               coalesce(lb.cover_image_url,
                   case when e.cover_image is not null
                        then '/v1/explore/' || left(lower(e.sha256), 24) || '/cover'
                        else null end),
               lb.playback_position_seconds, lb.is_finished, lb.is_favorite,
               lb.added_at, lb.updated_at
        from user_library_books lb
        join audiobook_editions e on e.id = lb.edition_id
        """;

    public IReadOnlyList<LibraryBook> List(Guid userID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            BookSelect + " where lb.user_id = $1 order by lb.updated_at desc;", connection);
        command.Parameters.AddWithValue(userID);
        using var reader = command.ExecuteReader();
        var books = new List<LibraryBook>();
        while (reader.Read()) books.Add(ReadBook(reader));
        return books;
    }

    public LibraryBook Upsert(Guid userID, LibraryBookUpsertRequest request)
    {
        using var connection = dataSource.OpenConnection();
        using var transaction = connection.BeginTransaction();
        byte[]? coverBytes = null;
        if (!string.IsNullOrWhiteSpace(request.CoverImageBase64))
        {
            try { coverBytes = Convert.FromBase64String(request.CoverImageBase64); }
            catch (FormatException) { throw new ArgumentException("The embedded cover is not valid base64."); }
            if (coverBytes.Length == 0 || coverBytes.Length > 2_000_000)
                throw new ArgumentException("The embedded cover is missing or too large.");
        }
        var editionID = UpsertEdition(connection, transaction, request.Fingerprint, coverBytes, request.CoverImageContentType);
        using (var command = new NpgsqlCommand("""
            insert into user_library_books(
                id, user_id, edition_id, title, author, narrator, cover_image_url,
                playback_position_seconds, is_finished, is_favorite, added_at, updated_at)
            values ($1, $2, $3, $4, $5, $6, $7, 0, false, false, now(), now())
            on conflict (user_id, edition_id) do update set
                title = excluded.title,
                author = excluded.author,
                narrator = excluded.narrator,
                cover_image_url = coalesce(excluded.cover_image_url, user_library_books.cover_image_url),
                updated_at = now();
            """, connection, transaction))
        {
            command.Parameters.AddWithValue(Guid.NewGuid());
            command.Parameters.AddWithValue(userID);
            command.Parameters.AddWithValue(editionID);
            command.Parameters.AddWithValue(CleanRequired(request.Title, "Untitled Audiobook"));
            AddNullable(command, CleanOptional(request.Author));
            AddNullable(command, CleanOptional(request.Narrator));
            AddNullable(command, CleanOptional(request.CoverImageURL));
            command.ExecuteNonQuery();
        }
        var book = FindBook(connection, transaction, userID, editionID)
            ?? throw new InvalidOperationException("The library book was not persisted.");
        transaction.Commit();
        return book;
    }

    public LibraryBook? UpdateProgress(
        Guid userID, Guid bookID, PlaybackProgressRequest request)
    {
        if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0) return null;
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update user_library_books set
                playback_position_seconds = $1,
                is_finished = $2,
                updated_at = now()
            where id = $3 and user_id = $4;
            """, connection);
        command.Parameters.AddWithValue(request.PositionSeconds);
        command.Parameters.AddWithValue(request.IsFinished);
        command.Parameters.AddWithValue(bookID);
        command.Parameters.AddWithValue(userID);
        return command.ExecuteNonQuery() == 0 ? null : FindBook(connection, null, userID, bookID: bookID);
    }

    public LibraryBook? UpdateFavorite(Guid userID, Guid bookID, bool isFavorite)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand("""
            update user_library_books set is_favorite = $1, updated_at = now()
            where id = $2 and user_id = $3;
            """, connection);
        command.Parameters.AddWithValue(isFavorite);
        command.Parameters.AddWithValue(bookID);
        command.Parameters.AddWithValue(userID);
        return command.ExecuteNonQuery() == 0 ? null : FindBook(connection, null, userID, bookID: bookID);
    }

    public bool DeleteBook(Guid userID, Guid bookID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "delete from user_library_books where id = $1 and user_id = $2;", connection);
        command.Parameters.AddWithValue(bookID);
        command.Parameters.AddWithValue(userID);
        return command.ExecuteNonQuery() > 0;
    }

    public IReadOnlyList<LibraryBookmark>? ListBookmarks(Guid userID, Guid bookID)
    {
        using var connection = dataSource.OpenConnection();
        if (!Owns(connection, userID, bookID)) return null;
        using var command = new NpgsqlCommand("""
            select id, library_book_id, position_seconds, title, note, created_at, updated_at
            from bookmarks where user_id = $1 and library_book_id = $2
            order by position_seconds;
            """, connection);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(bookID);
        using var reader = command.ExecuteReader();
        var bookmarks = new List<LibraryBookmark>();
        while (reader.Read()) bookmarks.Add(ReadBookmark(reader));
        return bookmarks;
    }

    public LibraryBookmark? AddBookmark(
        Guid userID, Guid bookID, BookmarkCreateRequest request)
    {
        if (!double.IsFinite(request.PositionSeconds) || request.PositionSeconds < 0) return null;
        using var connection = dataSource.OpenConnection();
        if (!Owns(connection, userID, bookID)) return null;
        using var command = new NpgsqlCommand("""
            insert into bookmarks(
                id, user_id, library_book_id, position_seconds, title, note, created_at, updated_at)
            values ($1, $2, $3, $4, $5, $6, now(), now())
            returning id, library_book_id, position_seconds, title, note, created_at, updated_at;
            """, connection);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(bookID);
        command.Parameters.AddWithValue(request.PositionSeconds);
        AddNullable(command, CleanOptional(request.Title));
        AddNullable(command, CleanOptional(request.Note, 2_000));
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBookmark(reader) : null;
    }

    public bool DeleteBookmark(Guid userID, Guid bookmarkID)
    {
        using var connection = dataSource.OpenConnection();
        using var command = new NpgsqlCommand(
            "delete from bookmarks where id = $1 and user_id = $2;", connection);
        command.Parameters.AddWithValue(bookmarkID);
        command.Parameters.AddWithValue(userID);
        return command.ExecuteNonQuery() > 0;
    }

    private static Guid UpsertEdition(
        NpgsqlConnection connection, NpgsqlTransaction transaction, BookFingerprint value,
        byte[]? coverBytes = null, string? coverContentType = null)
    {
        using var command = new NpgsqlCommand("""
            insert into audiobook_editions(
                id, fingerprint_version, sha256, file_size, duration_seconds, file_type,
                work_title, author, series_title, series_number, edition_type,
                part_number, total_parts, cover_image, cover_image_content_type, created_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8, $9, $10, $11, $12, $13, $14, $15, now())
            on conflict (fingerprint_version, sha256, file_size) do update set
                duration_seconds = coalesce(excluded.duration_seconds, audiobook_editions.duration_seconds),
                work_title = coalesce(excluded.work_title, audiobook_editions.work_title),
                author = coalesce(excluded.author, audiobook_editions.author),
                cover_image = coalesce(excluded.cover_image, audiobook_editions.cover_image),
                cover_image_content_type = coalesce(excluded.cover_image_content_type, audiobook_editions.cover_image_content_type)
            returning id;
            """, connection, transaction);
        command.Parameters.AddWithValue(Guid.NewGuid());
        command.Parameters.AddWithValue(value.Version);
        command.Parameters.AddWithValue(value.Sha256.ToLowerInvariant());
        command.Parameters.AddWithValue(value.FileSize);
        AddNullable(command, value.Duration);
        command.Parameters.AddWithValue(value.FileType);
        AddNullable(command, value.WorkTitle);
        AddNullable(command, value.Author);
        AddNullable(command, value.SeriesTitle);
        AddNullable(command, value.SeriesNumber);
        AddNullable(command, value.EditionType);
        AddNullable(command, value.PartNumber);
        AddNullable(command, value.TotalParts);
        AddNullable(command, coverBytes);
        AddNullable(command, coverContentType);
        return (Guid)(command.ExecuteScalar()
            ?? throw new InvalidOperationException("The audiobook edition was not persisted."));
    }

    private static LibraryBook? FindBook(
        NpgsqlConnection connection,
        NpgsqlTransaction? transaction,
        Guid userID,
        Guid? editionID = null,
        Guid? bookID = null)
    {
        var predicate = editionID is not null ? "lb.edition_id = $2" : "lb.id = $2";
        using var command = new NpgsqlCommand(
            BookSelect + $" where lb.user_id = $1 and {predicate};", connection, transaction);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue((object?)editionID ?? bookID!.Value);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadBook(reader) : null;
    }

    private static bool Owns(NpgsqlConnection connection, Guid userID, Guid bookID)
    {
        using var command = new NpgsqlCommand(
            "select exists(select 1 from user_library_books where user_id = $1 and id = $2);",
            connection);
        command.Parameters.AddWithValue(userID);
        command.Parameters.AddWithValue(bookID);
        return (bool)(command.ExecuteScalar() ?? false);
    }

    private static LibraryBook ReadBook(NpgsqlDataReader reader)
    {
        var fingerprint = new BookFingerprint(
            reader.GetInt32(1), reader.GetString(2).Trim(), reader.GetInt64(3),
            Nullable<double>(reader, 4), reader.GetString(5),
            NullableString(reader, 6), NullableString(reader, 7),
            NullableString(reader, 8), Nullable<int>(reader, 9),
            NullableString(reader, 10), Nullable<int>(reader, 11), Nullable<int>(reader, 12));
        return new LibraryBook(
            reader.GetGuid(0), fingerprint, reader.GetString(13),
            NullableString(reader, 14), NullableString(reader, 15),
            NullableString(reader, 16), reader.GetDouble(17), reader.GetBoolean(18),
            reader.GetBoolean(19), reader.GetFieldValue<DateTimeOffset>(20),
            reader.GetFieldValue<DateTimeOffset>(21));
    }

    private static LibraryBookmark ReadBookmark(NpgsqlDataReader reader) => new(
        reader.GetGuid(0), reader.GetGuid(1), reader.GetDouble(2),
        NullableString(reader, 3), NullableString(reader, 4),
        reader.GetFieldValue<DateTimeOffset>(5), reader.GetFieldValue<DateTimeOffset>(6));

    private static T? Nullable<T>(NpgsqlDataReader reader, int ordinal) where T : struct =>
        reader.IsDBNull(ordinal) ? null : reader.GetFieldValue<T>(ordinal);
    private static string? NullableString(NpgsqlDataReader reader, int ordinal) =>
        reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    private static void AddNullable(NpgsqlCommand command, object? value) =>
        command.Parameters.AddWithValue(value ?? DBNull.Value);
    private static string CleanRequired(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim()[..Math.Min(value.Trim().Length, 300)];
    private static string? CleanOptional(string? value, int maximum = 500) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim()[..Math.Min(value.Trim().Length, maximum)];
}
#endif
