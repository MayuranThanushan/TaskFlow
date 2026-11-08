using System.Text;
using System.Xml;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;

namespace TaskFlow.Api.Hosting;

// Supplied through the host's protected secret store, never the image or database.
public sealed class ReadOnlyKeyRing : IXmlRepository
{
    private readonly XElement[] elements;
    public ReadOnlyKeyRing(string encoded)
    {
        try
        {
            if (encoded.Length > 512 * 1024) throw new FormatException();
            var bytes = Convert.FromBase64String(encoded);
            using var input = new MemoryStream(bytes);
            using var reader = XmlReader.Create(input, new() { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, MaxCharactersInDocument = 384 * 1024 });
            var root = XElement.Load(reader);
            if (root.Name != "keyRing" || root.HasAttributes || !root.Elements().Any() || root.Elements().Any(e => e.Name != "key" && e.Name != "revocation")) throw new FormatException();
            elements = root.Elements().Select(e => new XElement(e)).ToArray();
        }
        catch (Exception e) when (e is FormatException or XmlException or InvalidOperationException)
        { throw new InvalidOperationException("The hosted read-only key ring is invalid. Restore the protected secret; its contents are not logged."); }
    }
    public IReadOnlyCollection<XElement> GetAllElements() => elements.Select(e => new XElement(e)).ToArray();
    public void StoreElement(XElement element, string friendlyName) => throw new InvalidOperationException("Hosted keys are read-only. Rotate through the protected hosting secret, retaining earlier keys.");
    public static string Encode(IEnumerable<XElement> values) => Convert.ToBase64String(Encoding.UTF8.GetBytes(new XElement("keyRing", values).ToString(SaveOptions.DisableFormatting)));
}
