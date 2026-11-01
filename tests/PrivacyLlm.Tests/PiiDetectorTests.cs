using PrivacyLlm.Core.Classification;

namespace PrivacyLlm.Tests;

public class PiiDetectorTests
{
    private readonly RegexPiiDetector _detector = new();

    private static string ValidAadhaar()
    {
        const string body = "23456789012";
        return body + Verhoeff.ComputeCheckDigit(body);
    }

    [Theory]
    [InlineData("Call me on 9876543210 tomorrow")]
    [InlineData("Phone: +91 98765 43210")]
    [InlineData("whatsapp 09876543210")]
    [InlineData("office 022-2345 6789")]
    public void Detects_indian_phone_numbers(string text) =>
        Assert.Contains(_detector.Detect(text), m => m.Type == PiiType.Phone);

    [Fact]
    public void Detects_email_and_pan_and_ifsc()
    {
        var types = _detector.Detect("Email ravi.k@example.co.in, PAN ABCPK1234F, IFSC HDFC0001234").Select(m => m.Type).ToList();
        Assert.Contains(PiiType.Email, types);
        Assert.Contains(PiiType.Pan, types);
        Assert.Contains(PiiType.Ifsc, types);
    }

    [Fact]
    public void Detects_upi_id_without_confusing_it_for_email()
    {
        var matches = _detector.Detect("Pay to ravi.kumar@okaxis please");
        Assert.Single(matches);
        Assert.Equal(PiiType.UpiId, matches[0].Type);
    }

    [Fact]
    public void Detects_aadhaar_only_when_checksum_valid()
    {
        var valid = ValidAadhaar();
        var invalidCheck = valid[..11] + ((valid[11] - '0' + 1) % 10);

        Assert.Contains(_detector.Detect($"Aadhaar {valid[..4]} {valid[4..8]} {valid[8..]}"), m => m.Type == PiiType.Aadhaar);
        Assert.DoesNotContain(_detector.Detect($"Order ref {invalidCheck}"), m => m.Type == PiiType.Aadhaar);
    }

    [Theory]
    [InlineData("Deliver to Mr. Ravi Kumar at the gate", "Ravi Kumar")]
    [InlineData("Customer: Priya Sharma complained about delay", "Priya Sharma")]
    [InlineData("Smt Lakshmi Iyer called", "Lakshmi Iyer")]
    public void Detects_names_introduced_by_honorific_or_label(string text, string expected) =>
        Assert.Contains(_detector.Detect(text), m => m.Type == PiiType.PersonName && m.Value == expected);

    [Fact]
    public void Detects_address_and_pincode()
    {
        var matches = _detector.Detect("Flat 302, Sai Residency, 14th Cross Road, Indiranagar, Bengaluru 560038");
        Assert.Contains(matches, m => m.Type == PiiType.Address);
        Assert.Contains(matches, m => m.Type == PiiType.Pincode && m.Value == "560038");
    }

    [Fact]
    public void Address_containing_a_phone_keeps_both()
    {
        var matches = _detector.Detect("House 12, MG Road, contact 9876543210, Pune");
        Assert.Contains(matches, m => m.Type == PiiType.Phone);
        Assert.Contains(matches, m => m.Type == PiiType.Address);
        Assert.False(matches.Zip(matches.Skip(1)).Any(p => p.First.End > p.Second.Start), "matches must not overlap");
    }

    [Fact]
    public void Plain_business_text_has_no_pii()
    {
        Assert.Empty(_detector.Detect("Write a product description for a stainless steel water bottle, 750 ml, BPA free."));
    }
}
