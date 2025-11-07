using System.Buffers;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;


namespace K4os.Orleans.NATS;

/// <summary>
/// Provides URL-safe Base64 encoding and decoding utilities for bytes, strings, and GUIDs.
/// </summary>
/// <remarks>
/// This implementation uses URL-safe Base64 encoding where '+' is replaced with '-' and '/' is replaced with '_'.
/// Padding characters ('=') are removed from encoded output and automatically added during decoding.
/// The class uses memory-efficient techniques with stack allocation for small buffers and array pooling for larger ones.
/// </remarks>
internal static class Url64
{
    private const int MAX_STACKALLOC_IN_BYTES = 8192;

    /// <summary>
    /// Calculates the maximum number of bytes that could result from decoding the specified text length.
    /// </summary>
    /// <param name="textLength">The length of the URL64-encoded text.</param>
    /// <returns>The maximum number of bytes that could be produced from decoding.</returns>
    /// <remarks>
    /// This method provides an upper bound for buffer allocation. The actual decoded length may be smaller due to padding.
    /// </remarks>
    public static int DecodeBytesMaxSize(int textLength) =>
        (((textLength + 3) & ~0x03) >> 2) * 3;

    /// <summary>
    /// Calculates the maximum number of characters needed to encode the specified number of bytes.
    /// </summary>
    /// <param name="bytesCount">The number of bytes to encode.</param>
    /// <returns>The maximum number of characters needed for URL64 encoding.</returns>
    /// <remarks>
    /// This method provides an upper bound for buffer allocation. The actual encoded length may be smaller due to removed padding.
    /// </remarks>
    public static int EncodeBytesMaxSize(int bytesCount) =>
        ((bytesCount + 2) / 3) << 2;

    /// <summary>
    /// Encodes the specified bytes into URL-safe Base64 characters and writes them to the output span.
    /// </summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <param name="chars">The span to write the encoded characters to.</param>
    /// <returns>The number of characters written to the output span.</returns>
    /// <exception cref="ArgumentException">Thrown when the output span is too small to contain the encoded result.</exception>
    /// <remarks>
    /// This method uses URL-safe Base64 encoding where '+' becomes '-', '/' becomes '_', and padding is removed.
    /// The output span must have sufficient capacity as determined by <see cref="EncodeBytesMaxSize"/>.
    /// </remarks>
    public static int EncodeBytes(ReadOnlySpan<byte> bytes, Span<char> chars)
    {
        var charsNeeded = EncodeBytesMaxSize(bytes.Length);
        ValidateBufferSize(chars, charsNeeded);
        EncodeUrl64(bytes, chars, out var actualChars);
        return actualChars;
    }

    /// <summary>
    /// Decodes URL-safe Base64 encoded characters into bytes and writes them to the output span.
    /// </summary>
    /// <param name="chars">The URL64-encoded characters to decode.</param>
    /// <param name="bytes">The span to write the decoded bytes to.</param>
    /// <returns>The number of bytes written to the output span.</returns>
    /// <exception cref="ArgumentException">Thrown when the output span is too small to contain the decoded result.</exception>
    /// <remarks>
    /// This method automatically adds padding if necessary and converts URL-safe characters back to standard Base64.
    /// The output span must have sufficient capacity as determined by <see cref="DecodeBytesMaxSize"/>.
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static int DecodeBytes(ReadOnlySpan<char> chars, Span<byte> bytes)
    {
        var bytesNeeded = DecodeBytesMaxSize(chars.Length);
        ValidateBufferSize(bytes, bytesNeeded);
        DecodeUrl64(chars, bytes, out var actualBytes);
        return actualBytes;
    }

    /// <summary>
    /// Encodes the specified bytes into a URL-safe Base64 string.
    /// </summary>
    /// <param name="bytes">The bytes to encode.</param>
    /// <returns>A URL-safe Base64 encoded string representation of the input bytes.</returns>
    /// <remarks>
    /// This method allocates a new string. For performance-critical scenarios with known buffer sizes,
    /// consider using the span-based overload to avoid string allocation.
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static string EncodeBytes(ReadOnlySpan<byte> bytes)
    {
        var maximumChars = EncodeBytesMaxSize(bytes.Length);
        var chars = TryRentBuffer<char>(maximumChars, out var charsPooled) ?? stackalloc char[maximumChars];
        var actualChars = EncodeBytes(bytes, chars);
        var result = new string(chars[..actualChars]);
        TryReturnBuffer(charsPooled);
        return result;
    }

    /// <summary>
    /// Decodes URL-safe Base64 encoded characters into a byte array.
    /// </summary>
    /// <param name="text">The URL64-encoded characters to decode.</param>
    /// <returns>A byte array containing the decoded data.</returns>
    /// <remarks>
    /// This method allocates a new byte array. The array is resized to the exact decoded length if necessary.
    /// For performance-critical scenarios with known buffer sizes, consider using the span-based overload.
    /// </remarks>
    public static byte[] DecodeBytes(ReadOnlySpan<char> text)
    {
        var bytesNeeded = DecodeBytesMaxSize(text.Length);
        var bytes = new byte[bytesNeeded];
        var actualBytes = DecodeBytes(text, bytes.AsSpan());
        if (actualBytes == bytesNeeded) return bytes;

        Array.Resize(ref bytes, actualBytes);
        return bytes;
    }

    /// <summary>
    /// Calculates the maximum number of characters needed to encode a string of the specified length.
    /// </summary>
    /// <param name="textLength">The length of the string to encode.</param>
    /// <returns>The maximum number of characters needed for URL64 encoding of the string.</returns>
    /// <remarks>
    /// This method accounts for UTF-8 encoding overhead and provides an upper bound for buffer allocation.
    /// </remarks>
    public static int EncodeStringMaxSize(int textLength) =>
        EncodeBytesMaxSize(Encoding.UTF8.GetMaxByteCount(textLength));

    /// <summary>
    /// Calculates the maximum number of characters that could result from decoding a URL64-encoded string.
    /// </summary>
    /// <param name="textLength">The length of the URL64-encoded text.</param>
    /// <returns>The maximum number of characters that could be produced from decoding to a UTF-8 string.</returns>
    /// <remarks>
    /// This method accounts for UTF-8 decoding overhead and provides an upper bound for buffer allocation.
    /// </remarks>
    public static int DecodeStringMaxSize(int textLength) =>
        Encoding.UTF8.GetMaxCharCount(DecodeBytesMaxSize(textLength));

    /// <summary>
    /// Encodes the specified string into URL-safe Base64 characters and writes them to the output span.
    /// </summary>
    /// <param name="text">The string to encode.</param>
    /// <param name="output">The span to write the encoded characters to.</param>
    /// <returns>The number of characters written to the output span.</returns>
    /// <exception cref="ArgumentException">Thrown when the output span is too small to contain the encoded result.</exception>
    /// <remarks>
    /// This method first converts the string to UTF-8 bytes, then applies URL-safe Base64 encoding.
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static int EncodeString(ReadOnlySpan<char> text, Span<char> output)
    {
        var maximumBytes = Encoding.UTF8.GetMaxByteCount(text.Length);
        var bytes = TryRentBuffer<byte>(maximumBytes, out var bytesPooled) ?? stackalloc byte[maximumBytes];
        var success = Encoding.UTF8.TryGetBytes(text, bytes, out var actualBytes);
        Debug.Assert(success);
        var result = EncodeBytes(bytes[..actualBytes], output);
        TryReturnBuffer(bytesPooled);
        return result;
    }

    /// <summary>
    /// Decodes URL-safe Base64 encoded characters into a string and writes them to the output span.
    /// </summary>
    /// <param name="encoded">The URL64-encoded characters to decode.</param>
    /// <param name="output">The span to write the decoded characters to.</param>
    /// <returns>The number of characters written to the output span.</returns>
    /// <exception cref="ArgumentException">Thrown when the output span is too small to contain the decoded result.</exception>
    /// <remarks>
    /// This method first decodes the URL-safe Base64 to bytes, then converts from UTF-8 to string characters.
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static int DecodeString(ReadOnlySpan<char> encoded, Span<char> output)
    {
        var bytesNeeded = DecodeBytesMaxSize(encoded.Length);
        var maximumChars = Encoding.UTF8.GetMaxCharCount(bytesNeeded);
        ValidateBufferSize(output, maximumChars);
        var bytes = TryRentBuffer<byte>(bytesNeeded, out var bytesPooled) ?? stackalloc byte[bytesNeeded];
        var actualBytes = DecodeBytes(encoded, bytes);
        var success = Encoding.UTF8.TryGetChars(bytes[..actualBytes], output, out var actualChars);
        Debug.Assert(success);
        TryReturnBuffer(bytesPooled);
        return actualChars;
    }

    /// <summary>
    /// Encodes the specified string into a URL-safe Base64 string.
    /// </summary>
    /// <param name="text">The string to encode.</param>
    /// <returns>A URL-safe Base64 encoded string representation of the input text.</returns>
    /// <remarks>
    /// This method allocates a new string. For performance-critical scenarios with known buffer sizes,
    /// consider using the span-based overload to avoid string allocation.
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static string EncodeString(string text)
    {
        var charsNeeded = EncodeStringMaxSize(text.Length);
        var chars = TryRentBuffer<char>(charsNeeded, out var charsPooled) ?? stackalloc char[charsNeeded];
        var actualChars = EncodeString(text.AsSpan(), chars);
        var result = new string(chars[..actualChars]);
        TryReturnBuffer(charsPooled);
        return result;
    }

    /// <summary>
    /// Decodes URL-safe Base64 encoded characters into a string.
    /// </summary>
    /// <param name="encoded">The URL64-encoded characters to decode.</param>
    /// <returns>A string containing the decoded text.</returns>
    /// <remarks>
    /// This method allocates a new string. For performance-critical scenarios with known buffer sizes,
    /// consider using the span-based overload to avoid string allocation.
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static string DecodeString(ReadOnlySpan<char> encoded)
    {
        var charsNeeded = DecodeStringMaxSize(encoded.Length);
        var chars = TryRentBuffer<char>(charsNeeded, out var charsPooled) ?? stackalloc char[charsNeeded];
        var actualChars = DecodeString(encoded, chars);
        var result = new string(chars[..actualChars]);
        TryReturnBuffer(charsPooled);
        return result;
    }

    private const int GUID_LENGTH = 16;
    private const int URL64_GUID_MAX_LENGTH = 24;

    /// <summary>
    /// Encodes the specified GUID into URL-safe Base64 characters and writes them to the output span.
    /// </summary>
    /// <param name="guid">The GUID to encode.</param>
    /// <param name="output">The span to write the encoded characters to.</param>
    /// <returns>The number of characters written to the output span.</returns>
    /// <exception cref="ArgumentException">Thrown when the output span is too small to contain the encoded result.</exception>
    /// <remarks>
    /// This method uses unsafe operations to directly encode the GUID bytes for optimal performance.
    /// The output span should have at least 24 characters capacity to handle the worst-case encoded GUID length.
    /// </remarks>
    public static unsafe int EncodeGuid(Guid guid, Span<char> output) =>
        EncodeBytes(new ReadOnlySpan<byte>(&guid, GUID_LENGTH), output);

    /// <summary>
    /// Encodes the specified GUID into a URL-safe Base64 string.
    /// </summary>
    /// <param name="guid">The GUID to encode.</param>
    /// <returns>A URL-safe Base64 encoded string representation of the GUID.</returns>
    /// <remarks>
    /// This method allocates a new string. For performance-critical scenarios,
    /// consider using the span-based overload to avoid string allocation.
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static string EncodeGuid(Guid guid)
    {
        const int charsNeeded = URL64_GUID_MAX_LENGTH; // Precomputed maximum size for encoded GUID
        Debug.Assert(EncodeBytesMaxSize(GUID_LENGTH) <= charsNeeded, "Precomputed size mismatch");
        Span<char> chars = stackalloc char[charsNeeded];
        var actualChars = EncodeGuid(guid, chars);
        return new string(chars[..actualChars]);
    }

    /// <summary>
    /// Decodes URL-safe Base64 encoded characters into a GUID.
    /// </summary>
    /// <param name="encoded">The URL64-encoded characters representing a GUID.</param>
    /// <returns>The decoded GUID.</returns>
    /// <exception cref="ArgumentException">Thrown when the encoded string is not a valid URL64-encoded GUID or is too long.</exception>
    /// <remarks>
    /// This method validates that the decoded data is exactly 16 bytes (the size of a GUID).
    /// Uses stack allocation for small buffers and array pooling for larger ones to optimize memory usage.
    /// </remarks>
    public static Guid DecodeGuid(ReadOnlySpan<char> encoded)
    {
        if (encoded.Length > URL64_GUID_MAX_LENGTH) ThrowNotUrl64Guid();
        const int bytesNeeded = 18; // Precomputed maximum size for decoded GUID (with padding)
        Debug.Assert(DecodeBytesMaxSize(URL64_GUID_MAX_LENGTH) <= bytesNeeded, "Precomputed size mismatch");
        Span<byte> bytes = stackalloc byte[bytesNeeded];
        var actualBytes = DecodeBytes(encoded, bytes);
        if (actualBytes != GUID_LENGTH) ThrowNotUrl64Guid();
        return new Guid(bytes[..GUID_LENGTH]);
    }

    private static void EncodeUrl64(ReadOnlySpan<byte> bytes, Span<char> chars, out int actualChars)
    {
        var totalBytes = bytes.Length;
        var blocks = totalBytes / 3;
        var tail = totalBytes - blocks * 3;

        actualChars = 0;

        // Encode full 3-byte blocks
        if (blocks > 0)
        {
            var bytesRead = blocks * 3;
            var success = Convert.TryToBase64Chars(bytes[..bytesRead], chars, out var charsWritten);
            Debug.Assert(success);
            bytes = bytes[bytesRead..];
            actualChars += charsWritten;
        }

        // Handle the tail (1 or 2 bytes)
        if (tail > 0)
        {
            Debug.Assert(tail < 3);
            Span<char> char4 = stackalloc char[4];
            var success = Convert.TryToBase64Chars(bytes, char4, out _);
            Debug.Assert(success);
            var significantChars = tail + 1; // 1 byte -> 2 chars, 2 bytes -> 3 chars
            char4[..significantChars].CopyTo(chars[actualChars..]);
            actualChars += significantChars;
        }

        chars.Replace('/', '_');
        chars.Replace('+', '-');
    }

    private static void DecodeUrl64(ReadOnlySpan<char> chars, Span<byte> bytes, out int actualBytes)
    {
        const int chunkSize = 4096; // Must be multiple of 4
        Debug.Assert(chunkSize % 4 == 0);

        actualBytes = 0;
        Span<char> char4K = stackalloc char[chunkSize];

        // handle full 4-char blocks
        while (true)
        {
            var chunk = chars.Length switch { >= chunkSize => chunkSize, var l => l & ~3 };
            if (chunk == 0) break;
            chars[..chunk].CopyTo(char4K);
            var block = char4K[..chunk];
            block.Replace('_', '/');
            block.Replace('-', '+');
            var success = Convert.TryFromBase64Chars(block, bytes, out var bytesWritten);
            Debug.Assert(success);
            chars = chars[chunk..];
            bytes = bytes[bytesWritten..];
            actualBytes += bytesWritten;
        }

        // Handle the last chunk (1-3 chars)
        if (chars.Length > 0)
        {
            Span<char> char4 = stackalloc char[4];
            char4.Fill('=');
            chars.CopyTo(char4);
            char4.Replace('_', '/');
            char4.Replace('-', '+');
            Span<byte> byte3 = stackalloc byte[3];
            var success = Convert.TryFromBase64Chars(char4, byte3, out var bytesWritten);
            Debug.Assert(success);
            byte3[..bytesWritten].CopyTo(bytes);
            actualBytes += bytesWritten;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static T[]? TryRentBuffer<T>(int count, out T[]? pooled)
    {
        var totalBytes = count * Unsafe.SizeOf<T>();
        pooled = totalBytes <= MAX_STACKALLOC_IN_BYTES ? null : ArrayPool<T>.Shared.Rent(count);
        return pooled;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void TryReturnBuffer<T>(T[]? pooled)
    {
        if (pooled is not null) ArrayPool<T>.Shared.Return(pooled);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void ValidateBufferSize<T>(Span<T> buffer, int sizeNeeded)
    {
        if (buffer.Length < sizeNeeded) ThrowInsufficientSpace(buffer.Length, sizeNeeded);
    }

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowInsufficientSpace(int actualLength, int expectedLength) =>
        throw new ArgumentException($"Insufficient buffer size: expected {expectedLength}, got {actualLength}.");

    [DoesNotReturn, MethodImpl(MethodImplOptions.NoInlining)]
    private static void ThrowNotUrl64Guid() =>
        throw new ArgumentException("Provided value is not Url64 encoded GUID");
}
