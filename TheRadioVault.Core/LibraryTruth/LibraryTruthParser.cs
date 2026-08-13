using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using TheRadioVault.Core.Services;

namespace TheRadioVault.Core.LibraryTruth;

/// <summary>
/// Parser V3 used by the Library Truth Engine. It never writes to the live
/// episode tables. Every conclusion is accompanied by evidence and ambiguous
/// files are allowed to remain unknown.
/// </summary>
public sealed class LibraryTruthParser
{
    public const string CurrentVersion = "0.32.0-alpha12-catalogue-style1-v3";

    private static readonly Regex IsoDate = Rx(@"(?<!\d)(?<year>(?:19|20)\d{2})[\s._-]+(?<month>\d{1,2})[\s._-]+(?<day>\d{1,2})(?!\d)");
    private static readonly Regex CompactIsoDate = Rx(@"(?<!\d)(?<year>(?:19|20)\d{2})(?<month>\d{2})(?<day>\d{2})(?!\d)");
    private static readonly Regex YearLastDate = Rx(@"(?<!\d)(?<first>\d{1,2})[\s._/-]+(?<second>\d{1,2})[\s._/-]+(?<year>(?:19|20)\d{2})(?!\d)");
    private static readonly Regex ShortYearDate = Rx(@"(?<!\d)(?<first>\d{1,2})[\s._/-]+(?<second>\d{1,2})[\s._/-]+(?<year>\d{2})(?!\d)");
    private static readonly Regex MonthDayOnly = Rx(@"(?<!\d)(?<month>\d{1,2})[\s._/-]+(?<day>\d{1,2})(?![\d._/-])");
    private static readonly Regex NamedMonthDate = Rx(@"(?<!\d)(?:(?<day>\d{1,2})(?:st|nd|rd|th)?[\s._,-]+(?<monthName>[A-Za-z]{3,9})|(?<monthName2>[A-Za-z]{3,9})[\s._,-]+(?<day2>\d{1,2})(?:st|nd|rd|th)?)[\s,._-]+(?<year>(?:19|20)\d{2})(?!\d)");
    private static readonly Regex RafCompact = Rx(@"\b(?:r\s*&?\s*f|raf)[\s._-]*(?<month>\d{2})(?<day>\d{2})[\s._-]*(?<year>\d{2})(?!\d)");
    private static readonly Regex RafMonthDayCompact = Rx(@"\b(?:r\s*&?\s*f|raf)[\s._-]*(?<month>\d{2})(?<day>\d{2})(?!\d)");
    private static readonly Regex InvalidZeroDate = Rx(@"(?<!\d)0[\s._/-]+\d{1,2}[\s._/-]+(?:19|20)\d{2}(?!\d)");
    private static readonly Regex IndexedShortYearDate = Rx(@"^\s*\d{1,3}\s*[_-]\s*(?<first>\d{1,2})[\s._/-]+(?<second>\d{1,2})[\s._/-]+(?<year>\d{2})(?!\d)");
    private static readonly Regex SourceIndexPrefix = Rx(@"^\s*\d{1,3}\s*[_-]+\s*");
    private static readonly Regex SourceIndexOnly = Rx(@"^\s*\d{1,3}\s*$");
    private static readonly Regex LeadingIndexBeforeNamedDate = Rx(@"^\s*\d{1,3}\s+(?=(?:\d{1,2}(?:st|nd|rd|th)?[\s,._-]+[A-Za-z]{3,9}|[A-Za-z]{3,9}[\s,._-]+\d{1,2}|(?:19|20)\d{2}[\s._-]+\d{1,2}[\s._-]+\d{1,2}|\d{1,2}[\s._-]+\d{1,2}[\s._-]+(?:19|20)?\d{2}))");

    private static readonly Regex PartOf = Rx(@"(?:^|[\s_.\-()\[\]])(?<kind>(?i:part|pt|segment|seg|disc|disk|cd|hour|hr))?[\s_.\-]*(?<part>\d{1,2})[\s_.\-]*(?:of|/)[\s_.\-]*(?<total>\d{1,2})(?:$|[\s_.\-()\[\]])");
    private static readonly Regex LabelledPart = Rx(@"(?:^|[\s_.\-()\[\]])(?<kind>part|pt|segment|seg|disc|disk|cd|hour|hr)[\s_.\-]*(?<part>\d{1,2})(?:$|[\s_.\-()\[\]])");
    private static readonly Regex CompactLabelledPart = Rx(@"(?:^|[\s_.\-()\[\]])(?<kind>p|pt|part|seg|segment)(?<part>\d{1,2})(?:$|[\s_.\-()\[\]])");
    private static readonly Regex RomanPart = new(@"(?:^|[\s_.\-()\[\]])(?:(?<kind>(?i:part|pt|segment|seg|disc|disk|cd|hour|hr))[\s_.\-]*)?(?<roman>I{1,3}|IV|V|VI{0,3}|IX|X)(?:[\s_.\-()\[\]]*)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex LetterPart = Rx(@"(?:^|[\s_.\-()\[\]])(?:(?:part|pt|segment|seg)[\s_.\-]*)?(?<letter>[ab])(?:[\s_.\-()\[\]]*)$");

    private static readonly Regex OpieRadio = Rx(@"\bopie\s*radio(?:\s+edition)?\b");
    private static readonly Regex BenningtonOr = Rx(@"(?:^|[\s_.\-()\[\]])or(?=(?:[\s_.\-()\[\]]|\d{2,3}k|$))");
    private static readonly Regex Midday = Rx(@"\b(?:midday|mid-day|noon|lunchtime|mid\s+show|mid)\b");
    private static readonly Regex Morning = Rx(@"\b(?:morning|am|a\.m\.)\b");
    private static readonly Regex Evening = Rx(@"\b(?:afternoon|evening|pm|p\.m\.|eve)\b");
    private static readonly Regex Late = Rx(@"\b(?:late\s+show|late)\b");
    private static readonly Regex AmClock = Rx(@"\b\d{1,2}(?::\d{2})?\s*a\.?m\.?\b");
    private static readonly Regex PmClock = Rx(@"\b\d{1,2}(?::\d{2})?\s*p\.?m\.?\b");

    public LibraryTruthInterpretation Parse(LibraryTruthFileInput input, LibraryTruthFolderContext context)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(context);

        var filename = input.FilenameWithoutExtension;
        var evidence = new List<LibraryTruthEvidence>();
        var warnings = new List<LibraryTruthWarning>();
        evidence.AddRange(context.Evidence);

        var collection = DetectCollection(filename, input.Path, input.AssignedCollectionName, context, evidence);
        if (!collection.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase) && ContainsAfroMarker(filename))
        {
            evidence.Add(new LibraryTruthEvidence("programme-format", "AFRO Show", 96, "filename",
                "AFRO identifies a cross-show programme format; the explicit parent show remains the collection identity."));
        }

        var dateResult = DetectDate(filename, input.Path, collection, context);
        evidence.AddRange(dateResult.Evidence);
        warnings.AddRange(dateResult.Warnings);

        var hasLeadingNamedIndex = LeadingIndexBeforeNamedDate.IsMatch(filename);
        var working = filename;
        foreach (var matchedText in dateResult.MatchedTexts)
            working = RemoveFirst(working, matchedText);
        working = SourceIndexPrefix.Replace(working, " ");
        if (hasLeadingNamedIndex)
            working = SourceIndexOnly.Replace(working, " ");

        var part = DetectMultipart(working);
        if (part.IsMultipart)
        {
            evidence.Add(new LibraryTruthEvidence("part", part.PartNumber.ToString(CultureInfo.InvariantCulture), 95,
                "filename", part.Reasoning));
            working = RemoveFirst(working, part.MatchedText);
        }

        working = NormalizeSemanticSeparators(working);
        var slot = DetectSlot(working, collection);
        if (!string.IsNullOrWhiteSpace(slot.Display))
        {
            evidence.Add(new LibraryTruthEvidence("slot", slot.Display, slot.Score, "filename", slot.Reasoning));
            working = RemoveFirst(working, slot.MatchedText);
        }

        working = RemoveCollectionAliases(working, collection);
        var headline = CleanHeadline(working, slot.Display);
        if (!string.IsNullOrWhiteSpace(headline))
            evidence.Add(new LibraryTruthEvidence("headline", headline, 55, "filename", "Descriptive text remained after structural and technical tokens were removed."));

        var confidenceScore = CalculateConfidence(collection, dateResult, slot, part, context, warnings);
        var confidence = confidenceScore switch
        {
            >= 88 => LibraryTruthConfidence.High,
            >= 68 => LibraryTruthConfidence.Probable,
            >= 45 => LibraryTruthConfidence.Low,
            _ => LibraryTruthConfidence.Unknown
        };

        var canonicalSlot = BroadcastSlotNormalizer.Canonicalize(slot.Display);
        var canonicalKey = LibraryTruthIdentity.Build(collection, dateResult.Date, canonicalSlot, input.MediaFileId, input.FullHash);
        var currentKey = LibraryTruthIdentity.Build(
            input.CurrentCollectionName,
            input.CurrentAirDate,
            BroadcastSlotNormalizer.Canonicalize(input.CurrentBroadcastSlot),
            input.CurrentEpisodeId);
        var (disposition, changeSummary) = CompareWithCurrent(input, collection, dateResult.Date, slot.Display,
            part.PartNumber, part.TotalParts, canonicalKey, currentKey, warnings);

        return new LibraryTruthInterpretation
        {
            Input = input,
            ParserVersion = CurrentVersion,
            CollectionName = collection,
            AirDate = dateResult.Date,
            DateConfidence = dateResult.Confidence,
            BroadcastSlot = slot.Display,
            CanonicalSlot = canonicalSlot,
            PartNumber = part.PartNumber,
            TotalParts = part.TotalParts,
            MultipartKind = part.Kind,
            Headline = headline,
            ConfidenceScore = confidenceScore,
            Confidence = confidence,
            CanonicalBroadcastKey = canonicalKey,
            CurrentIdentityKey = currentKey,
            Disposition = disposition,
            ChangeSummary = changeSummary,
            Evidence = evidence,
            Warnings = warnings
        };
    }

    public static string? DetectExplicitCollection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var semantic = NormalizeSemanticSeparators(text);
        return DetectExplicitPrimaryCollection(semantic)
               ?? (ContainsAfroMarker(semantic) ? "AFRO Show" : null);
    }

    private static string? DetectExplicitPrimaryCollection(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var semantic = NormalizeSemanticSeparators(text);
        foreach (var rule in LibraryTruthRuleCatalog.Collections.Where(
                     rule => !rule.CanonicalName.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase)))
            if (rule.Aliases.Any(alias => alias.IsMatch(semantic))) return rule.CanonicalName;
        return null;
    }

    private static bool ContainsAfroMarker(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        var semantic = NormalizeSemanticSeparators(text);
        var rule = LibraryTruthRuleCatalog.Collections.FirstOrDefault(
            item => item.CanonicalName.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase));
        return rule is not null && rule.Aliases.Any(alias => alias.IsMatch(semantic));
    }

    private static string DetectCollection(
        string filename,
        string path,
        string assignedCollection,
        LibraryTruthFolderContext context,
        ICollection<LibraryTruthEvidence> evidence)
    {
        var explicitFilename = DetectExplicitPrimaryCollection(filename);
        if (!string.IsNullOrWhiteSpace(explicitFilename))
        {
            evidence.Add(new LibraryTruthEvidence("show", explicitFilename, 100, "filename",
                "An explicit parent-show alias was found in the filename. It takes priority over folder assignment and programme-format labels."));
            return explicitFilename;
        }

        var explicitPath = DetectExplicitPrimaryCollection(path);
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            evidence.Add(new LibraryTruthEvidence("show", explicitPath, 84, "folder path",
                "A parent-show alias was found in the containing folder path."));
            return explicitPath;
        }

        if (!string.IsNullOrWhiteSpace(assignedCollection) &&
            !assignedCollection.Equals("Auto detect", StringComparison.OrdinalIgnoreCase) &&
            !assignedCollection.Equals("Unsorted", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new LibraryTruthEvidence("show", assignedCollection, 76, "library assignment",
                "The registered archive root is assigned to this show."));
            return assignedCollection;
        }

        if (!string.IsNullOrWhiteSpace(context.DominantCollectionName) &&
            !context.DominantCollectionName.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new LibraryTruthEvidence("show", context.DominantCollectionName, 70, "folder context",
                "Most explicitly labelled neighbouring files belong to this parent show."));
            return context.DominantCollectionName;
        }

        if (ContainsAfroMarker(filename) || ContainsAfroMarker(path))
        {
            evidence.Add(new LibraryTruthEvidence("show", "AFRO Show", 86,
                ContainsAfroMarker(filename) ? "filename" : "folder path",
                "No parent show is named, so the dedicated AFRO archive identity is used."));
            return "AFRO Show";
        }

        if (!string.IsNullOrWhiteSpace(assignedCollection) &&
            !assignedCollection.Equals("Auto detect", StringComparison.OrdinalIgnoreCase))
        {
            evidence.Add(new LibraryTruthEvidence("show", assignedCollection, 68, "library assignment",
                "The registered archive root supplies the only available collection identity."));
            return assignedCollection;
        }

        if (!string.IsNullOrWhiteSpace(context.DominantCollectionName))
        {
            evidence.Add(new LibraryTruthEvidence("show", context.DominantCollectionName, 64, "folder context",
                "Neighbouring files supply the only available collection identity."));
            return context.DominantCollectionName;
        }

        return "Unsorted";
    }

    private static DateDetection DetectDate(string filename, string path, string collection, LibraryTruthFolderContext context)
    {
        var candidates = new List<DateCandidate>();
        var warnings = new List<LibraryTruthWarning>();
        var matchedTexts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddRegexDates(filename, IsoDate, 100, "ISO year-first date", "YMD", candidates, matchedTexts);
        AddRegexDates(filename, CompactIsoDate, 98, "Compact ISO date", "YMD", candidates, matchedTexts);
        AddNamedMonthDates(filename, candidates, matchedTexts);
        AddRafCompactDates(filename, candidates, matchedTexts);
        AddRafMonthDayDates(filename, context.YearHint ?? FindYearInPath(path), candidates, matchedTexts);

        foreach (Match match in IndexedShortYearDate.Matches(filename))
        {
            if (!TryNumber(match, "first", out var first) || !TryNumber(match, "second", out var second) || !TryNumber(match, "year", out var shortYear)) continue;
            var year = shortYear >= 80 ? 1900 + shortYear : 2000 + shortYear;
            AddOrderedCandidate(first, second, year, collection, context, match, 92, candidates, matchedTexts);
        }

        foreach (Match match in YearLastDate.Matches(filename))
        {
            if (!TryNumber(match, "first", out var first) || !TryNumber(match, "second", out var second) || !TryNumber(match, "year", out var year)) continue;
            AddOrderedCandidate(first, second, year, collection, context, match, 94, candidates, matchedTexts);
        }

        foreach (Match match in ShortYearDate.Matches(filename))
        {
            if (!TryNumber(match, "first", out var first) || !TryNumber(match, "second", out var second) || !TryNumber(match, "year", out var shortYear)) continue;
            var year = shortYear >= 80 ? 1900 + shortYear : 2000 + shortYear;
            AddOrderedCandidate(first, second, year, collection, context, match, 88, candidates, matchedTexts);
        }

        if (context.YearHint.HasValue)
        {
            foreach (Match match in MonthDayOnly.Matches(filename))
            {
                if (!TryNumber(match, "month", out var month) || !TryNumber(match, "day", out var day)) continue;
                if (TryCreateDate(context.YearHint.Value, month, day, out var date))
                {
                    candidates.Add(new DateCandidate(date, 72, match.Value,
                        $"Month and day were combined with the {context.YearHint.Value} year established by the folder path.", "folder-year"));
                    matchedTexts.Add(match.Value);
                }
            }
        }

        if (InvalidZeroDate.IsMatch(filename))
            warnings.Add(new LibraryTruthWarning("malformed-date", "The filename contains a zero month/day component and was not guessed automatically."));

        var pathYear = FindYearInPath(path);
        if (!context.YearHint.HasValue && pathYear.HasValue)
        {
            foreach (Match match in MonthDayOnly.Matches(filename))
            {
                if (!TryNumber(match, "month", out var month) || !TryNumber(match, "day", out var day)) continue;
                if (TryCreateDate(pathYear.Value, month, day, out var date))
                {
                    candidates.Add(new DateCandidate(date, 68, match.Value,
                        $"Month and day were combined with year {pathYear.Value} from the containing path.", "path-year"));
                    matchedTexts.Add(match.Value);
                }
            }
        }

        if (candidates.Count == 0)
        {
            warnings.Add(new LibraryTruthWarning("unknown-date", "No trustworthy date could be recovered from the filename or folder context."));
            return new DateDetection(null, "Unknown", Array.Empty<LibraryTruthEvidence>(), warnings, matchedTexts.ToArray());
        }

        var grouped = candidates
            .GroupBy(x => x.Date)
            .Select(group => new
            {
                Date = group.Key,
                Score = group.Max(x => x.Score),
                Candidates = group.OrderByDescending(x => x.Score).ToArray()
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.Date)
            .ToArray();

        var best = grouped[0];
        if (grouped.Length > 1 && grouped[1].Score >= best.Score - 8)
        {
            warnings.Add(new LibraryTruthWarning("date-conflict",
                $"The available evidence supports more than one date ({best.Date:yyyy-MM-dd} and {grouped[1].Date:yyyy-MM-dd})."));
            return new DateDetection(null, "Ambiguous", best.Candidates.Select(ToEvidence).ToArray(), warnings, matchedTexts.ToArray());
        }

        if (grouped.Length > 1)
            warnings.Add(new LibraryTruthWarning("discarded-date-claim",
                $"A weaker date claim ({grouped[1].Date:yyyy-MM-dd}) was retained as evidence but not selected.", false));

        var confidence = best.Score >= 90 ? "High" : best.Score >= 70 ? "Probable" : "Low";
        return new DateDetection(best.Date, confidence, best.Candidates.Select(ToEvidence).ToArray(), warnings, matchedTexts.ToArray());
    }

    private static void AddRegexDates(
        string filename,
        Regex regex,
        int score,
        string reasoning,
        string order,
        ICollection<DateCandidate> candidates,
        ISet<string> matchedTexts)
    {
        foreach (Match match in regex.Matches(filename))
        {
            if (!TryNumber(match, "year", out var year) || !TryNumber(match, "month", out var month) || !TryNumber(match, "day", out var day)) continue;
            if (!TryCreateDate(year, month, day, out var date)) continue;
            candidates.Add(new DateCandidate(date, score, match.Value, reasoning, order));
            matchedTexts.Add(match.Value);
        }
    }

    private static void AddNamedMonthDates(string filename, ICollection<DateCandidate> candidates, ISet<string> matchedTexts)
    {
        foreach (Match match in NamedMonthDate.Matches(filename))
        {
            var monthText = match.Groups["monthName"].Success ? match.Groups["monthName"].Value : match.Groups["monthName2"].Value;
            var dayText = match.Groups["day"].Success ? match.Groups["day"].Value : match.Groups["day2"].Value;
            if (!int.TryParse(dayText, out var day) || !int.TryParse(match.Groups["year"].Value, out var rawYear)) continue;
            if (!DateTime.TryParseExact(monthText, new[] { "MMM", "MMMM" }, CultureInfo.InvariantCulture,
                    DateTimeStyles.AllowWhiteSpaces, out var monthValue)) continue;
            var year = rawYear < 100 ? (rawYear >= 80 ? 1900 + rawYear : 2000 + rawYear) : rawYear;
            if (!TryCreateDate(year, monthValue.Month, day, out var date)) continue;
            candidates.Add(new DateCandidate(date, 100, match.Value, "A complete named-month date was found.", "named-month"));
            matchedTexts.Add(match.Value);
        }
    }

    private static void AddRafCompactDates(string filename, ICollection<DateCandidate> candidates, ISet<string> matchedTexts)
    {
        foreach (Match match in RafCompact.Matches(filename))
        {
            if (!TryNumber(match, "month", out var month) || !TryNumber(match, "day", out var day) || !TryNumber(match, "year", out var shortYear)) continue;
            var year = shortYear >= 80 ? 1900 + shortYear : 2000 + shortYear;
            if (!TryCreateDate(year, month, day, out var date)) continue;
            candidates.Add(new DateCandidate(date, 96, match.Value, "Recognised the compact RaF MMDD YY archive convention.", "raf-compact"));
            matchedTexts.Add(match.Value);
        }
    }

    private static void AddRafMonthDayDates(
        string filename,
        int? yearHint,
        ICollection<DateCandidate> candidates,
        ISet<string> matchedTexts)
    {
        if (!yearHint.HasValue) return;
        foreach (Match match in RafMonthDayCompact.Matches(filename))
        {
            if (!TryNumber(match, "month", out var month) || !TryNumber(match, "day", out var day)) continue;
            if (!TryCreateDate(yearHint.Value, month, day, out var date)) continue;
            candidates.Add(new DateCandidate(date, 82, match.Value,
                $"Recognised the compact RaF MMDD convention and combined it with year {yearHint.Value} from the folder path.",
                "raf-month-day-folder-year"));
            matchedTexts.Add(match.Value);
        }
    }

    private static void AddOrderedCandidate(
        int first,
        int second,
        int year,
        string collection,
        LibraryTruthFolderContext context,
        Match match,
        int baseScore,
        ICollection<DateCandidate> candidates,
        ISet<string> matchedTexts)
    {
        var preferUs = context.DateOrder.Equals("US", StringComparison.OrdinalIgnoreCase) || IsKnownUsShow(collection);
        if (first > 12 && second <= 12)
        {
            if (TryCreateDate(year, second, first, out var unambiguousDmy))
                candidates.Add(new DateCandidate(unambiguousDmy, baseScore + 2, match.Value, "The first component exceeds 12, making day-month-year unambiguous.", "DMY"));
        }
        else if (second > 12 && first <= 12)
        {
            if (TryCreateDate(year, first, second, out var unambiguousMdy))
                candidates.Add(new DateCandidate(unambiguousMdy, baseScore + 4, match.Value, "The second component exceeds 12, making month-day-year unambiguous.", "MDY"));
        }
        else
        {
            if (preferUs && TryCreateDate(year, first, second, out var mdy))
            {
                candidates.Add(new DateCandidate(mdy, baseScore, match.Value, "Folder/show context establishes the US month-day-year archive convention.", "MDY-context"));
            }
            else if (context.DateOrder.Equals("DMY", StringComparison.OrdinalIgnoreCase) && TryCreateDate(year, second, first, out var dmy))
            {
                candidates.Add(new DateCandidate(dmy, baseScore, match.Value, "Neighbouring filenames establish the day-month-year convention.", "DMY-context"));
            }
            else
            {
                if (TryCreateDate(year, first, second, out var ambiguousMdy))
                    candidates.Add(new DateCandidate(ambiguousMdy, baseScore - 12, match.Value, "Possible month-day-year interpretation; no folder convention confirms it.", "MDY-ambiguous"));
                if (TryCreateDate(year, second, first, out var ambiguousDmy))
                    candidates.Add(new DateCandidate(ambiguousDmy, baseScore - 12, match.Value, "Possible day-month-year interpretation; no folder convention confirms it.", "DMY-ambiguous"));
            }
        }
        matchedTexts.Add(match.Value);
    }

    private static SlotDetection DetectSlot(string filename, string collection)
    {
        var opie = OpieRadio.Match(filename);
        if (opie.Success) return new SlotDetection("OpieRadio Edition", 100, opie.Value, "The filename explicitly names the additional OpieRadio broadcast.");

        var shorthand = BenningtonOr.Match(filename);
        if (collection.Equals("Bennington", StringComparison.OrdinalIgnoreCase) && shorthand.Success)
            return new SlotDetection("OpieRadio Edition", 92, shorthand.Value, "Bennington ‘OR’ shorthand identifies the additional OpieRadio broadcast.");

        var midday = Midday.Match(filename);
        if (midday.Success) return new SlotDetection("Midday", 94, midday.Value, "A midday/noon slot marker was found.");

        var hasAmClock = AmClock.IsMatch(filename);
        var hasPmClock = PmClock.IsMatch(filename);
        if (hasAmClock && !hasPmClock) return new SlotDetection("Morning show", 92, AmClock.Match(filename).Value, "A scheduled AM time was found.");
        if (hasPmClock && !hasAmClock) return new SlotDetection("Evening show", 92, PmClock.Match(filename).Value, "A scheduled PM time was found.");

        var morning = Morning.Match(filename);
        if (morning.Success) return new SlotDetection("Morning show", 90, morning.Value, "An AM/morning slot marker was found.");
        var evening = Evening.Match(filename);
        if (evening.Success) return new SlotDetection("Evening show", 90, evening.Value, "A PM/afternoon/evening slot marker was found.");
        var late = Late.Match(filename);
        if (late.Success) return new SlotDetection("Late show", 88, late.Value, "A late-show slot marker was found.");
        return SlotDetection.None;
    }

    private static MultipartDetection DetectMultipart(string filename)
    {
        var match = PartOf.Match(filename);
        if (match.Success && TryNumber(match, "part", out var part) && TryNumber(match, "total", out var total) && part > 0 && total >= part && total <= 99)
            return new MultipartDetection(part, total, NormalizeKind(match.Groups["kind"].Value), match.Value,
                "Recognised a multipart x-of-y marker.");

        match = LabelledPart.Match(filename);
        if (match.Success && TryNumber(match, "part", out part) && part > 0)
            return new MultipartDetection(part, null, NormalizeKind(match.Groups["kind"].Value), match.Value,
                "Recognised a labelled multipart marker.");

        match = CompactLabelledPart.Match(filename);
        if (match.Success && TryNumber(match, "part", out part) && part > 0)
            return new MultipartDetection(part, null, "Part", match.Value,
                "Recognised a compact P1/P2-style multipart marker.");

        match = RomanPart.Match(filename);
        if (match.Success && TryRoman(match.Groups["roman"].Value, out part))
            return new MultipartDetection(part, null, NormalizeKind(match.Groups["kind"].Value), match.Value,
                "Recognised a trailing Roman-numeral segment marker.");

        match = LetterPart.Match(filename);
        if (match.Success)
        {
            part = match.Groups["letter"].Value.Equals("b", StringComparison.OrdinalIgnoreCase) ? 2 : 1;
            return new MultipartDetection(part, 2, "Part", match.Value, "Recognised a trailing A/B segment marker.");
        }

        // Alpha6 still does not promote a bare trailing number at file-parse time.
        // A lone "... 1" is frequently a track or version marker. The shadow
        // recording planner may promote 1,2,... only when sibling filenames
        // establish a contiguous multipart convention within one family.

        return MultipartDetection.None;
    }

    private static string CleanHeadline(string value, string slot)
    {
        var cleaned = value;
        foreach (var rule in LibraryTruthRuleCatalog.TechnicalNoisePatterns)
            cleaned = rule.Replace(cleaned, " ");
        if (!string.IsNullOrWhiteSpace(slot))
        {
            cleaned = OpieRadio.Replace(cleaned, " ");
            cleaned = BenningtonOr.Replace(cleaned, " ");
            cleaned = Midday.Replace(cleaned, " ");
            cleaned = Morning.Replace(cleaned, " ");
            cleaned = Evening.Replace(cleaned, " ");
            cleaned = Late.Replace(cleaned, " ");
        }
        cleaned = Regex.Replace(cleaned, @"[\[\]{}()]", " ");
        cleaned = Regex.Replace(cleaned, @"[_.,]+", " ");
        cleaned = Regex.Replace(cleaned, @"\s*[-–—]+\s*", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim(' ', '-', '_', '.', ',', ';', ':');

        if (string.IsNullOrWhiteSpace(cleaned) || cleaned.Length < 4) return string.Empty;
        if (cleaned.Equals("show", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("the show", StringComparison.OrdinalIgnoreCase) ||
            cleaned.Equals("radio show", StringComparison.OrdinalIgnoreCase)) return string.Empty;
        if (cleaned.All(character => char.IsDigit(character) || char.IsWhiteSpace(character))) return string.Empty;
        return cleaned;
    }

    private static int CalculateConfidence(
        string collection,
        DateDetection date,
        SlotDetection slot,
        MultipartDetection part,
        LibraryTruthFolderContext context,
        IReadOnlyCollection<LibraryTruthWarning> warnings)
    {
        var score = 10;
        if (!collection.Equals("Unsorted", StringComparison.OrdinalIgnoreCase)) score += 25;
        if (date.Date.HasValue) score += date.Confidence == "High" ? 45 : date.Confidence == "Probable" ? 34 : 20;
        if (!string.IsNullOrWhiteSpace(slot.Display)) score += Math.Min(10, slot.Score / 10);
        if (part.IsMultipart) score += 6;
        if (context.FileCount >= 3) score += 4;
        score -= warnings.Count(x => x.NeedsReview) * 18;
        return Math.Clamp(score, 0, 100);
    }

    private static (string Disposition, string Summary) CompareWithCurrent(
        LibraryTruthFileInput input,
        string collection,
        DateOnly? airDate,
        string slot,
        int part,
        int? totalParts,
        string proposedKey,
        string currentKey,
        IReadOnlyCollection<LibraryTruthWarning> warnings)
    {
        if (!airDate.HasValue) return ("Needs attention", "Date remains unknown; the live library is unchanged.");

        var changes = new List<string>();
        if (!string.Equals(input.CurrentCollectionName, collection, StringComparison.OrdinalIgnoreCase))
            changes.Add($"show: {Display(input.CurrentCollectionName)} → {collection}");
        if (input.CurrentAirDate != airDate)
            changes.Add($"date: {input.CurrentAirDate?.ToString("yyyy-MM-dd") ?? "unknown"} → {airDate.Value:yyyy-MM-dd}");
        if (!BroadcastSlotNormalizer.Equivalent(input.CurrentBroadcastSlot, slot))
            changes.Add($"slot: {Display(input.CurrentBroadcastSlot, "standard")} → {Display(slot, "standard")}");
        if (Math.Max(1, input.CurrentPartNumber) != Math.Max(1, part) || input.CurrentTotalParts != totalParts)
            changes.Add($"segment: {PartDisplay(input.CurrentPartNumber, input.CurrentTotalParts)} → {PartDisplay(part, totalParts)}");

        if (warnings.Any(x => x.Code == "date-conflict"))
            return ("Needs attention", "Conflicting date evidence must be resolved before adoption.");
        if (input.CurrentAirDate is null && airDate.HasValue)
            return ("Recovered date", string.Join("; ", changes));
        if (!string.Equals(proposedKey, currentKey, StringComparison.OrdinalIgnoreCase) || changes.Count > 0)
            return (part > 1 || totalParts.HasValue ? "Multipart correction" : "Proposed correction", string.Join("; ", changes));
        return ("Unchanged", "Parser V3 agrees with the live library identity.");
    }

    private static string RemoveCollectionAliases(string value, string selectedCollection)
    {
        var result = NormalizeSemanticSeparators(value);
        var selectedRule = LibraryTruthRuleCatalog.Collections.FirstOrDefault(
            rule => rule.CanonicalName.Equals(selectedCollection, StringComparison.OrdinalIgnoreCase));
        if (selectedRule is not null)
            foreach (var alias in selectedRule.Aliases) result = alias.Replace(result, " ");

        // Parent-show aliases are structural even when AFRO remains as a
        // programme-format headline.
        if (!selectedCollection.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var rule in LibraryTruthRuleCatalog.Collections.Where(
                         rule => !rule.CanonicalName.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase)))
                foreach (var alias in rule.Aliases) result = alias.Replace(result, " ");
        }
        return result;
    }

    private static string NormalizeSemanticSeparators(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var normalized = Regex.Replace(value, @"[_\.]+", " ");
        normalized = Regex.Replace(normalized, @"\s*[-–—]+\s*", " ");
        normalized = Regex.Replace(normalized, @"\b(?<meridiem>[ap])\s+m\b", "${meridiem}m",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static string RemoveFirst(string value, string? matched)
    {
        if (string.IsNullOrWhiteSpace(matched)) return value;
        var index = value.IndexOf(matched, StringComparison.OrdinalIgnoreCase);
        return index < 0 ? value : value.Remove(index, matched.Length).Insert(index, " ");
    }

    private static bool TryCreateDate(int year, int month, int day, out DateOnly date)
    {
        date = default;
        if (year is < 1980 or > 2035 || month is < 1 or > 12 || day < 1) return false;
        try
        {
            date = new DateOnly(year, month, day);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            return false;
        }
    }

    private static int? FindYearInPath(string path)
    {
        var yearPattern = new Regex(@"(?<!\d)(?<year>(?:19|20)\d{2})(?!\d)",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);
        foreach (var component in ArchivePath.Components(path).Reverse())
        {
            var matches = yearPattern.Matches(component);
            for (var index = matches.Count - 1; index >= 0; index--)
                if (int.TryParse(matches[index].Groups["year"].Value, out var year) && year is >= 1980 and <= 2035)
                    return year;
        }
        return null;
    }

    private static bool IsKnownUsShow(string collection)
        => collection.Equals("AFRO Show", StringComparison.OrdinalIgnoreCase)
           || KnownShowCatalog.UsesUsArchiveDateOrder(collection);

    private static LibraryTruthEvidence ToEvidence(DateCandidate candidate)
        => new("date", candidate.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), candidate.Score,
            candidate.Source, candidate.Reasoning);

    private static bool TryNumber(Match match, string group, out int value)
        => int.TryParse(match.Groups[group].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    private static bool TryRoman(string value, out int result)
    {
        result = value.Trim().ToUpperInvariant() switch
        {
            "I" => 1, "II" => 2, "III" => 3, "IV" => 4, "V" => 5,
            "VI" => 6, "VII" => 7, "VIII" => 8, "IX" => 9, "X" => 10,
            _ => 0
        };
        return result > 0;
    }

    private static string NormalizeKind(string value) => value.Trim().ToLowerInvariant() switch
    {
        "hour" or "hr" => "Hour",
        "segment" or "seg" => "Segment",
        "disc" or "disk" or "cd" => "Disc",
        _ => "Part"
    };

    private static string Display(string? value, string fallback = "unknown")
        => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string PartDisplay(int part, int? total)
        => total.HasValue ? $"part {Math.Max(1, part)} of {total}" : $"part {Math.Max(1, part)}";

    private static Regex Rx(string pattern)
        => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record DateCandidate(DateOnly Date, int Score, string MatchedText, string Reasoning, string Source);
    private sealed record DateDetection(
        DateOnly? Date,
        string Confidence,
        IReadOnlyList<LibraryTruthEvidence> Evidence,
        IReadOnlyList<LibraryTruthWarning> Warnings,
        IReadOnlyList<string> MatchedTexts);
    private sealed record SlotDetection(string Display, int Score, string MatchedText, string Reasoning)
    {
        public static SlotDetection None { get; } = new(string.Empty, 0, string.Empty, string.Empty);
    }
    private sealed record MultipartDetection(int PartNumber, int? TotalParts, string Kind, string MatchedText, string Reasoning)
    {
        public static MultipartDetection None { get; } = new(1, null, string.Empty, string.Empty, string.Empty);
        public bool IsMultipart => PartNumber > 1 || TotalParts.HasValue || !string.IsNullOrWhiteSpace(Kind);
    }
}
