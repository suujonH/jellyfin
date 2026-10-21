using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using MediaBrowser.Controller.NetworkStorage;

namespace Jellyfin.Server.Implementations.NetworkStorage;

/// <summary>
/// Reads and writes the XML bodies of the S3 requests the server sends itself. Elements are matched by their local name,
/// since S3-compatible services do not all answer in the S3 namespace.
/// </summary>
internal static class S3Xml
{
    private static readonly XNamespace _namespace = "http://s3.amazonaws.com/doc/2006-03-01/";

    /// <summary>
    /// Reads a page of a <c>ListObjectsV2</c> answer.
    /// </summary>
    /// <param name="body">The answer.</param>
    /// <returns>The objects of the page, and the token of the next page, or <c>null</c> when this is the last one.</returns>
    /// <exception cref="System.Xml.XmlException">The answer is not XML.</exception>
    /// <exception cref="FormatException">The answer is not a listing.</exception>
    public static (IReadOnlyList<StorageObject> Objects, string? NextContinuationToken) ReadListPage(byte[] body)
    {
        var root = Load(body, "ListBucketResult");
        var objects = new List<StorageObject>();
        foreach (var contents in Children(root, "Contents"))
        {
            var key = Value(contents, "Key") ?? throw new FormatException("A listed object has no key.");
            var size = Value(contents, "Size") ?? throw new FormatException("Listed object " + key + " has no size.");
            var lastModified = Value(contents, "LastModified") ?? throw new FormatException("Listed object " + key + " has no time.");
            objects.Add(new StorageObject(
                key,
                long.Parse(size, NumberStyles.None, CultureInfo.InvariantCulture),
                DateTime.Parse(lastModified, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)));
        }

        if (!string.Equals(Value(root, "IsTruncated"), "true", StringComparison.OrdinalIgnoreCase))
        {
            return (objects, null);
        }

        // Without a token the next page would be this one again.
        var token = Value(root, "NextContinuationToken");
        return string.IsNullOrEmpty(token) ? throw new FormatException("A truncated listing has no continuation token.") : (objects, token);
    }

    /// <summary>
    /// Writes the body of a <c>DeleteObjects</c> request in quiet mode, whose answer lists only the objects that failed.
    /// </summary>
    /// <param name="keys">The keys.</param>
    /// <returns>The body, in UTF-8.</returns>
    public static byte[] WriteDelete(IEnumerable<string> keys)
    {
        var delete = new XElement(
            _namespace + "Delete",
            new XElement(_namespace + "Quiet", "true"),
            keys.Select(key => new XElement(_namespace + "Object", new XElement(_namespace + "Key", key))));

        // Line breaks are written as references, which XML keeps as they are, so that every key reads back the same.
        var settings = new XmlWriterSettings { OmitXmlDeclaration = true, NewLineHandling = NewLineHandling.Entitize, Encoding = new UTF8Encoding(false) };
        using var stream = new MemoryStream();
        using (var writer = XmlWriter.Create(stream, settings))
        {
            delete.WriteTo(writer);
        }

        return stream.ToArray();
    }

    /// <summary>
    /// Reads the objects a <c>DeleteObjects</c> answer says failed.
    /// </summary>
    /// <param name="body">The answer.</param>
    /// <returns>The key, code and message of each failure.</returns>
    /// <exception cref="System.Xml.XmlException">The answer is not XML.</exception>
    /// <exception cref="FormatException">The answer is not a result of DeleteObjects.</exception>
    public static IReadOnlyList<(string Key, string Code, string Message)> ReadDeleteErrors(byte[] body)
        => Children(Load(body, "DeleteResult"), "Error")
            .Select(error => (Value(error, "Key") ?? string.Empty, Value(error, "Code") ?? string.Empty, Value(error, "Message") ?? string.Empty))
            .ToArray();

    // Another document, such as the login page of a proxy in front of the storage, would read as nothing listed or deleted.
    private static XElement Load(byte[] body, string rootName)
    {
        using var stream = new MemoryStream(body, false);
        var root = XDocument.Load(stream).Root;
        return root is not null && string.Equals(root.Name.LocalName, rootName, StringComparison.Ordinal)
            ? root
            : throw new FormatException("The answer is not a " + rootName + ".");
    }

    private static IEnumerable<XElement> Children(XElement parent, string name)
        => parent.Elements().Where(e => string.Equals(e.Name.LocalName, name, StringComparison.Ordinal));

    private static string? Value(XElement parent, string name) => Children(parent, name).FirstOrDefault()?.Value;
}
