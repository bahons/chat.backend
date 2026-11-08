using System.Globalization;
using System.Text;

namespace chat.IntegrationTests;

internal static class VerificationFiles
{
    public static byte[] Pdf(string comment = "verification-test")
    {
        var text = new StringBuilder($"%PDF-1.4\n% {comment}\n");
        var offsets = new List<int> { 0 };
        foreach (var body in new[]
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R] /Count 1 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"
        })
        {
            offsets.Add(text.Length);
            text.Append(CultureInfo.InvariantCulture, $"{offsets.Count - 1} 0 obj\n{body}\nendobj\n");
        }

        var xref = text.Length;
        text.Append(CultureInfo.InvariantCulture, $"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (var offset in offsets.Skip(1))
        {
            text.Append(CultureInfo.InvariantCulture, $"{offset:D10} 00000 n \n");
        }

        text.Append(CultureInfo.InvariantCulture, $"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
