using System.Globalization;
using Bogus;
using PatientFeedback.Dispatch;
using PatientFeedback.Domain;
using PatientFeedback.Scoring;

namespace PatientFeedback.Synthetic;

/// <summary>
/// Synthetic visits and answers (Bogus, Turkish locale). Names are generated, patient references and visit ids are
/// random, and phone numbers use the 0532 000 xx xx block, which is never assigned to a subscriber.
/// All data is synthetic; nothing is derived from real patients.
/// </summary>
public sealed class VisitGenerator
{
    public static readonly IReadOnlyList<string> Departments =
        ["Emergency", "Cardiology", "Orthopaedics", "Paediatrics", "Internal Medicine", "Radiology", "Obstetrics"];

    // A deliberately weaker department so the demo panel has something to find.
    private const string WeakDepartment = "Emergency";

    private readonly Faker _faker;

    public VisitGenerator(int seed)
    {
        _faker = new Faker("tr") { Random = new Randomizer(seed) };
    }

    public VisitCompletedEvent Visit(DateTimeOffset completedAt, double consentRate = 0.92, double whatsAppShare = 0.8)
    {
        var first = _faker.Name.FirstName();
        var last = _faker.Name.LastName();
        return new VisitCompletedEvent
        {
            Type = VisitCompletedEvent.EventType,
            VisitId = "V" + _faker.Random.Long(10_000_000_000, 99_999_999_999).ToString(CultureInfo.InvariantCulture),
            PatientRef = "P" + _faker.Random.Long(100_000_000, 999_999_999).ToString(CultureInfo.InvariantCulture),
            PatientName = first + " " + last,
            Phone = "+90532000" + _faker.Random.Int(0, 9999).ToString("D4", CultureInfo.InvariantCulture),
            Department = _faker.PickRandom(Departments.ToArray()),
            CompletedAt = completedAt,
            SurveyConsent = _faker.Random.Bool((float)consentRate),
            PreferredChannel = _faker.Random.Bool((float)whatsAppShare) ? Channel.WhatsApp : Channel.Sms,
        };
    }

    /// <summary>
    /// Plausible answers: NPS skewed high with a detractor tail, CSAT and CES correlated with NPS, and the weak
    /// department shifted down. About one in four answers carries a comment.
    /// </summary>
    public SurveyAnswers Answers(string department)
    {
        var shift = department == WeakDepartment ? -2.5 : 0;
        var mood = Math.Clamp(_faker.Random.Double() * _faker.Random.Double() * -10 + 10 + shift, 0, 10); // skewed to 10
        var nps = (int)Math.Round(mood);
        var csat = Math.Clamp((int)Math.Round(1 + (mood / 10 * 4) + _faker.Random.Double(-0.7, 0.7)), 1, 5);
        var ces = Math.Clamp((int)Math.Round(1 + (mood / 10 * 6) + _faker.Random.Double(-1, 1)), 1, 7);
        string? comment = null;
        if (_faker.Random.Bool(0.25f))
        {
            comment = nps <= 6
                ? _faker.PickRandom(NegativeComments)
                : _faker.PickRandom(PositiveComments);
        }

        return new SurveyAnswers(nps, csat, ces, comment);
    }

    private static readonly string[] NegativeComments =
    [
        "Bekleme süresi çok uzundu.",
        "Randevu saatimde muayene olamadım.",
        "Danışmada kimse yardımcı olmadı.",
        "Sonuçlarım için üç kez aramam gerekti.",
        "Otopark ve yönlendirme yetersizdi.",
    ];

    private static readonly string[] PositiveComments =
    [
        "Doktorumuz çok ilgiliydi, teşekkürler.",
        "Hemşireler çok nazikti.",
        "Hızlı ve düzenli bir süreçti.",
        "Temiz ve sakin bir ortam.",
    ];
}
