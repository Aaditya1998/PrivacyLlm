namespace PrivacyLlm.Core.Classification.Names;

/// <summary>
/// Every weight and cue list in one place. Each token's evidence is summed and squashed:
/// score = sigmoid(sum - <see cref="Offset"/>). So a bare unambiguous given name (+2.0) lands in
/// the uncertain band, and one strong cue lifts it to definite.
/// </summary>
public static class NameSignals
{
    public const double Offset = 2.5;

    // What the word itself says.
    public const double GivenName = 2.0;
    public const double Surname = 1.0;
    public const double AmbiguousName = 0.5;  // Asha, Raja, Ganesh, Tata: needs context

    // Strong cues: these alone make even an unknown word a likely name.
    public const double KinshipSuffix = 2.5;  // Anil bhai, Sharma ji, Meena didi
    public const double HonorificPrefix = 2.5; // Shri, Smt, Thiru (Mr/Mrs are already caught by the regex detector)
    public const double Greeting = 2.5;        // Hi Priya, / Thanks Anil
    public const double SignOff = 2.5;         // Regards,\nKavita Rao
    public const double RelationNoun = 2.0;    // my friend Ravi, driver Suresh

    // Supporting cues.
    public const double AdjacentName = 2.0;    // given + surname, or two given names
    public const double SpeechVerbAfter = 1.5; // Ravi said, Fatima paid
    public const double InteractionBefore = 1.5; // spoke to Ravi, ask Ramesh, cc Priya
    public const double Initial = 1.5;         // R. Suresh, S Kavitha
    public const double HindiPostposition = 1.0; // Anil ne, Priya ko, Ravi ka
    public const double TitleCaseMidSentence = 1.0;
    public const double Vocative = 1.0;        // "Ravi, please check" at the start of a line
    public const double Possessive = 1.5;      // Sagar's laptop
    public const double FromAfter = 1.5;       // Grace from accounts, Bharat from the Nashik branch
    public const double CoordinatedName = 1.0; // Sneha, Rahul and Ibrahim: listed next to a clear name

    // Penalties and weaker lexical evidence.
    public const double LowercaseInMixedCase = -1.0; // "kumkum" in a properly capitalised sentence is probably a word
    public const double FoldedGivenName = 1.0;       // only a spelling-variant match ("mega" ~ "megha")
    public const double FoldedSurname = 0.5;

    /// <summary>Cue weight at or above which an ambiguous lowercase word is still considered.</summary>
    public const double StrongCue = 1.5;

    public static readonly HashSet<string> KinshipSuffixes = Set(
        "bhai", "bhaiya", "bhaiyya", "didi", "di", "da", "ji", "jee", "sir", "madam", "mam", "maam", "saab", "sahab", "saheb", "bhabhi",
        "uncle", "aunty", "auntie", "anna", "akka", "amma", "appa", "garu", "babu", "dada", "chacha", "chachi", "mama", "mami",
        "kaka", "kaki", "tai", "bhau", "bhaijaan", "baji", "aapa", "chechi", "chettan", "etta", "mausi", "nani", "dadi", "bua");

    public static readonly HashSet<string> HonorificPrefixes = Set(
        "shri", "sri", "shrimati", "smt", "kumari", "km", "thiru", "thirumathi", "tmt", "janab", "sardar", "sardarni",
        "pandit", "ustad", "sushri", "kum", "mr", "mrs", "ms", "miss", "dr", "prof", "capt", "col", "adv", "er");

    public static readonly HashSet<string> Greetings = Set(
        "hi", "hello", "hey", "dear", "namaste", "namaskar", "namaskaram", "vanakkam", "thanks", "thank", "thankyou", "hii", "helo",
        "salaam", "salam", "assalamualaikum", "pranam", "good", "morning", "evening", "afternoon", "welcome", "congrats", "congratulations");

    public static readonly HashSet<string> SignOffs = Set(
        "regards", "best", "cheers", "thanks", "thank", "sincerely", "warmly", "yours", "from", "by", "dhanyavad", "dhanyawad", "shukriya", "love");

    public static readonly HashSet<string> RelationNouns = Set(
        "friend", "brother", "sister", "wife", "husband", "son", "daughter", "mother", "father", "mom", "dad", "colleague", "manager",
        "customer", "client", "driver", "agent", "rider", "executive", "technician", "patient", "tenant", "landlord", "neighbour",
        "neighbor", "cousin", "nephew", "niece", "uncle", "aunt", "boss", "teammate", "intern", "dost", "beta", "beti", "bhaiya",
        "behen", "bahen", "partner", "fiance", "fiancee", "guest", "caller", "applicant", "candidate", "employee", "student", "teacher",
        "doctor", "nurse", "consignee", "recipient", "sender", "buyer", "seller", "vendor", "supplier", "plumber", "electrician", "maid", "cook");

    public static readonly HashSet<string> SpeechVerbs = Set(
        "said", "says", "told", "asked", "asks", "called", "calls", "mentioned", "replied", "complained", "wants", "wanted", "paid",
        "pays", "ordered", "booked", "cancelled", "canceled", "will", "is", "was", "has", "had", "bola", "boli", "bole", "kaha",
        "bataya", "boliye", "needs", "confirmed", "sent", "received", "picked", "emailed", "messaged", "texted", "requested",
        "reported", "approved", "joined", "can", "could", "would", "should", "did", "does", "agreed", "refused", "returned",
        "visited", "came", "went", "signed", "raised", "shared", "wrote", "spoke", "phoned", "rang", "pinged", "forwarded");

    /// <summary>Verbs specific enough that "[Unknown word] asked" makes the unknown word a possible name.</summary>
    public static readonly HashSet<string> StrongSpeechVerbs = Set(
        "said", "told", "asked", "called", "mentioned", "replied", "complained", "wants", "wanted", "paid", "ordered", "booked",
        "cancelled", "canceled", "bola", "boli", "kaha", "bataya", "emailed", "messaged", "texted", "requested", "confirmed",
        "approved", "phoned", "rang", "pinged", "agreed", "refused");

    /// <summary>Greetings that are almost always followed by a name or "team"; the rest ("thanks", "welcome") need punctuation after the name.</summary>
    public static readonly HashSet<string> PureGreetings = Set(
        "hi", "hello", "hey", "dear", "namaste", "namaskar", "namaskaram", "vanakkam", "hii", "helo", "salaam", "salam", "assalamualaikum", "pranam");

    public static readonly HashSet<string> InteractionWords = Set(
        "met", "meet", "ask", "tell", "inform", "contact", "call", "ping", "remind", "cc", "with", "thank", "told", "asked", "informed",
        "contacted", "pinged", "reminded", "assign", "assigned", "notify", "notified", "email", "emailed", "message", "messaged", "whatsapp",
        "text", "loop", "escalate");

    /// <summary>"to" counts as an interaction cue only after one of these verbs: "spoke to Ravi", "handed to Priya".</summary>
    public static readonly HashSet<string> VerbsBeforeTo = Set(
        "spoke", "talked", "speak", "talk", "send", "sent", "give", "gave", "hand", "handed", "assign", "assigned", "forward", "forwarded",
        "reply", "replied", "report", "reported", "deliver", "delivered", "pay", "paid", "transfer", "transferred", "refund", "refunded",
        "explained", "explain", "mention", "mentioned", "introduce", "introduced", "escalate", "escalated", "wrote", "write", "say", "said");

    /// <summary>Never treated as names: "from April to June" would otherwise look like a recipient.</summary>
    public static readonly HashSet<string> Calendar = Set(
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december",
        "monday", "tuesday", "wednesday", "thursday", "friday", "saturday", "sunday");

    /// <summary>Words that end the clause after "assign to X" / "for X", so X is the object of the sentence.</summary>
    public static readonly HashSet<string> ClauseEnders = Set(
        "in", "before", "after", "from", "on", "at", "by", "and", "today", "tomorrow", "tonight", "asap", "please", "pls", "also", "too", "now");

    /// <summary>Before an unknown capitalised word, these suggest a recipient: "assign the ticket to Neikhrielie."</summary>
    public static readonly HashSet<string> RecipientPrepositions = Set("to", "for", "with", "cc", "ask", "tell", "ping", "remind", "thank");

    public static readonly HashSet<string> HindiPostpositions = Set("ne", "ko", "ka", "ki", "ke", "se", "wala", "wali", "wale", "kaa", "kii");

    /// <summary>A name followed by one of these is part of a place, institution, festival, product or object.</summary>
    public static readonly HashSet<string> NonPersonSuffixes = Set(
        "nagar", "road", "rd", "marg", "chowk", "colony", "vihar", "puram", "bagh", "ganj", "pur", "abad", "layout", "enclave",
        "sector", "block", "street", "lane", "gali", "cross", "circle", "bazaar", "bazar", "market", "mall", "complex", "tower",
        "towers", "apartments", "apartment", "society", "residency", "heights", "hospital", "clinic", "university", "college",
        "school", "vidyalaya", "institute", "academy", "stadium", "airport", "station", "terminus", "junction", "temple", "mandir",
        "masjid", "mosque", "church", "gurudwara", "gurdwara", "dargah", "math", "ashram", "park", "garden", "gardens", "lake",
        "river", "dam", "bridge", "setu", "ghat", "beach", "hill", "hills", "fort", "palace", "museum", "library", "hall", "bhavan",
        "bhawan", "sadan", "motors", "steel", "industries", "ltd", "limited", "pvt", "private", "group", "corp", "corporation",
        "trust", "foundation", "bank", "finance", "finserv", "insurance", "jewellers", "jewelers", "stores", "store", "sweets",
        "silks", "textiles", "traders", "enterprises", "agencies", "pharma", "pharmacy", "medicals", "express", "travels",
        "transport", "logistics", "foods", "restaurant", "hotel", "dhaba", "bhojanalaya", "jayanti", "jayanthi", "puja",
        "chaturthi", "navami", "ashtami", "utsav", "mela", "festival", "idol", "murti", "statue", "coin", "aarti",
        "chalisa", "stotram", "mantra", "yojana", "scheme", "award", "trophy", "cup", "salt", "masala", "oil", "atta",
        "biscuits", "bhujia", "namkeen", "brand", "products", "mart", "electronics", "mobiles", "studio", "studios", "films",
        "productions", "cinema", "theatre", "theater", "nursing", "diagnostics", "labs", "dental", "tutorials", "classes",
        "coaching", "kendra", "samiti", "sangh", "parishad", "nigam", "district", "taluk", "tehsil", "village", "gram",
        "superfast", "rajdhani", "smarak", "darshan", "yatra", "panchami", "purnima", "amavasya",
        "ekadashi", "dham", "peeth", "kund", "sale", "collection",
        "edition", "series", "combo", "size", "sizes", "shade", "shades", "colour", "color", "variant");

    /// <summary>A deity-flagged name after one of these is the deity, not a person: "Lord Shiva", "Jai Hanuman".</summary>
    public static readonly HashSet<string> DeityPrefixes = Set(
        "lord", "bhagwan", "bhagavan", "god", "goddess", "maa", "mata", "jai", "jay", "om", "shree", "devi", "swami", "baba", "sant", "saint");

    private static HashSet<string> Set(params string[] words) => new(words, StringComparer.Ordinal);
}
