using TheRadioVault.Core.Models;

using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;

namespace TheRadioVault.Core.Services;

public sealed partial class FilenameParserService
{
    public const string CurrentParserVersion = "0.32.0-alpha13-catalogue-dates2";
    private static readonly string[] NumericFormats =
    {
        "yyyy-MM-dd", "yyyy.MM.dd", "yyyy_MM_dd", "yyyyMMdd",
        "MM-dd-yyyy", "M-d-yyyy", "MM-d-yyyy", "M-dd-yyyy",
        "MM_dd_yyyy", "M_d_yyyy", "MM_d_yyyy", "M_dd_yyyy",
        "MM.dd.yyyy", "M.d.yyyy", "MM.d.yyyy", "M.dd.yyyy",
        "dd-MM-yyyy", "d-M-yyyy", "dd-M-yyyy", "d-MM-yyyy",
        "dd_MM_yyyy", "d_M_yyyy", "dd_M_yyyy", "d_MM_yyyy",
        "dd.MM.yyyy", "d.M.yyyy", "dd.M.yyyy", "d.MM.yyyy",
        "MM/dd/yyyy", "M/d/yyyy", "MM/d/yyyy", "M/dd/yyyy",
        "dd/MM/yyyy", "d/M/yyyy", "dd/M/yyyy", "d/MM/yyyy"
    };

    private static readonly string[] NamedMonthFormats =
    {
        "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy",
        "MMM d yyyy", "MMM dd yyyy", "MMMM d yyyy", "MMMM dd yyyy",
        "d MMM yy", "dd MMM yy", "d MMMM yy", "dd MMMM yy",
        "MMM d yy", "MMM dd yy", "MMMM d yy", "MMMM dd yy"
    };

    public ParsedFilename Parse(string path) => Parse(path, null);

    public ParsedFilename Parse(string path, FilenameParseContext? context)
    {
        var filename = ArchivePath.GetFileNameWithoutExtension(path);
        var collectionFromFilename = DetectCollection(filename.ToLowerInvariant());
        var assignedCollection = KnownShowCatalog.Normalize(context?.AssignedCollectionName);
        var result = new ParsedFilename
        {
            // Match the scanner's authority order: an explicit show token in the
            // filename wins, then the user's folder assignment, then path inference.
            // Carrying the assignment into parsing is important for catalogue-style
            // files whose guest/title text does not itself name the show.
            CollectionName = collectionFromFilename ?? assignedCollection ?? DetectCollection(path.ToLowerInvariant()),
            CollectionDetectedFromFilename = !string.IsNullOrWhiteSpace(collectionFromFilename),
            ParserVersion = CurrentParserVersion
        };

        var partInfo = MultipartParser.Detect(filename);
        result.PartNumber = partInfo.PartNumber;
        result.TotalParts = partInfo.TotalParts;
        result.MultipartKind = partInfo.Kind;
        result.MultipartReasoning = partInfo.Reasoning;

        var station = StationDictionary.Detect(path + " " + filename);
        if (station is not null)
        {
            result.StationCandidate = station.CanonicalName;
            result.StationConfidence = station.Confidence;
            result.StationReasoning = station.Reasoning;
        }

        var workingFilename = filename;
        if (context?.ShouldIgnoreLeadingSequence == true && TryRemoveLeadingSequence(workingFilename, out var withoutSequence, out var sequence))
        {
            workingFilename = withoutSequence;
            result.IgnoredLeadingSequence = sequence;
        }
        else if (TryRemoveLeadingSequenceBeforeCompleteDate(workingFilename, out withoutSequence, out sequence))
        {
            workingFilename = withoutSequence;
            result.IgnoredLeadingSequence = sequence;
        }

        var dateMatch = TryFindDate(workingFilename, result.CollectionName)
            ?? TryFindDateFromArchiveContext(path, workingFilename, result.CollectionName);
        if (dateMatch is not null)
        {
            result.AirDate = dateMatch.Value.Date;
            result.DateConfidence = dateMatch.Value.Confidence;
            result.DateReasoning = dateMatch.Value.Reasoning;
            result.MatchedGrammar = dateMatch.Value.Grammar;
        }

        var headlineSource = dateMatch is null
            ? workingFilename
            : workingFilename.Remove(dateMatch.Value.Index, dateMatch.Value.Length);
        if (partInfo.IsMultipart && !string.IsNullOrWhiteSpace(partInfo.MatchedText))
            headlineSource = headlineSource.Replace(partInfo.MatchedText, " ", StringComparison.Ordinal);

        result.BroadcastType = DetectBroadcastType(headlineSource);
        var edition = DetectEdition(headlineSource);
        result.Edition = edition.Value;
        result.EditionReasoning = edition.Reasoning;
        result.BroadcastSlot = DetectBroadcastSlot(headlineSource);

        var headline = ExtractHeadline(headlineSource, result.CollectionName, result.BroadcastSlot);
        result.HeadlineCandidate = headline.Value;
        result.HeadlineConfidence = headline.Confidence;
        result.HeadlineReasoning = headline.Reasoning;
        if (result.IgnoredLeadingSequence.HasValue)
            result.HeadlineReasoning += $" Ignored leading folder sequence number {result.IgnoredLeadingSequence.Value}.";
        var confidence = CalculateMetadataConfidence(result);
        result.MetadataConfidence = confidence.Score;
        result.MetadataConfidenceReasoning = confidence.Reasoning;
        return result;
    }

    public FilenameParseContext AnalyseFolder(IEnumerable<string> paths)
    {
        var filenames = paths.Select(ArchivePath.GetFileNameWithoutExtension).Where(x => !string.IsNullOrWhiteSpace(x)).ToArray();
        if (filenames.Length < 3) return FilenameParseContext.None;

        var numbers = new List<int>();
        var matches = 0;
        foreach (var filename in filenames)
        {
            var match = LeadingSequenceRegex().Match(filename!);
            if (!match.Success || !int.TryParse(match.Groups["sequence"].Value, out var value)) continue;
            matches++;
            numbers.Add(value);
        }

        var coverage = (double)matches / filenames.Length;
        if (coverage < 0.70 || numbers.Count < 3) return FilenameParseContext.None;

        var distinct = numbers.Distinct().Count();
        var ordered = numbers.Zip(numbers.Skip(1), (a, b) => b >= a).Count(x => x);
        var monotonicRatio = numbers.Count <= 1 ? 0 : (double)ordered / (numbers.Count - 1);
        var plausibleSequence = distinct >= Math.Max(3, numbers.Count * 0.75) && monotonicRatio >= 0.70;

        return plausibleSequence
            ? new FilenameParseContext(true, $"{matches} of {filenames.Length} files use a monotonic leading sequence number.")
            : FilenameParseContext.None;
    }

    private static (DateTime Date, int Index, int Length, string Confidence, string Reasoning, string Grammar)? TryFindDate(string filename, string? collectionName = null)
    {
        var compactRonFez = RonFezCompactDateRegex().Match(filename);
        if (compactRonFez.Success &&
            int.TryParse(compactRonFez.Groups["month"].Value, out var compactMonth) &&
            int.TryParse(compactRonFez.Groups["day"].Value, out var compactDay) &&
            int.TryParse(compactRonFez.Groups["year"].Value, out var compactYear))
        {
            compactYear += compactYear >= 80 ? 1900 : 2000;
            try
            {
                var date = new DateTime(compactYear, compactMonth, compactDay);
                var dateStart = compactRonFez.Groups["month"].Index;
                var dateEnd = compactRonFez.Groups["year"].Index + compactRonFez.Groups["year"].Length;
                return (date, dateStart, dateEnd - dateStart, "High",
                    "Recognised the Ron & Fez compact RaF MMDD YY filename grammar.", "Ron & Fez compact (RaF MMDD YY)");
            }
            catch (ArgumentOutOfRangeException)
            {
                // Continue through the normal date rules when the compact digits are not a valid date.
            }
        }

        // Prefer a complete named-month date with an explicit year before shorter
        // numeric patterns. This prevents a leading archive sequence number from
        // being mistaken for the day when a filename contains e.g.
        // "11 August 21, 2009 (1)".
        foreach (Match match in NamedMonthDateRegex().Matches(filename))
        {
            var candidate = OrdinalSuffixRegex().Replace(match.Value, "$1");
            candidate = Regex.Replace(candidate, @"[,._-]+", " ");
            candidate = Regex.Replace(candidate, @"\s+", " ").Trim();
            foreach (var format in NamedMonthFormats)
            {
                if (!DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture,
                        DateTimeStyles.AllowWhiteSpaces, out var date) || date.Year is < 1980 or > 2035)
                    continue;

                if (date.Year < 100) date = date.AddYears(date.Year >= 80 ? 1900 : 2000);
                return (date, match.Index, match.Length, "High",
                    "Recognised a complete human-readable date containing a named month.", $"Named month date ({format})");
            }
        }


        // Common North American radio-archive convention: M-D-YY.
        // Restrict automatic interpretation to known US shows so ambiguous
        // two-digit dates are not guessed globally. Parenthesised slot markers
        // such as “(midday)” are parsed separately as structured metadata.
        if (KnownShowCatalog.UsesUsArchiveDateOrder(collectionName))
        {
            foreach (Match match in TwoDigitUsDateRegex().Matches(filename))
            {
                if (!int.TryParse(match.Groups["month"].Value, out var month) ||
                    !int.TryParse(match.Groups["day"].Value, out var day) ||
                    !int.TryParse(match.Groups["year"].Value, out var shortYear))
                    continue;

                var year = shortYear >= 80 ? 1900 + shortYear : 2000 + shortYear;
                try
                {
                    var date = new DateTime(year, month, day);
                    return (date, match.Index, match.Length, "High",
                        "Recognised the common US radio-archive M-D-YY date pattern.",
                        "US short date (M-D-YY)");
                }
                catch (ArgumentOutOfRangeException)
                {
                    // Continue with other rules if the digits are not a valid date.
                }
            }
        }

        foreach (Match match in DateRegex().Matches(filename))
        {
            var candidate = match.Value.Replace(' ', '-').Replace('_', '-').Replace('.', '-').Replace('/', '-');
            foreach (var format in NumericFormats)
            {
                if (!DateTime.TryParseExact(candidate, format, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ||
                    date.Year is < 1980 or > 2035)
                    continue;

                var dayFirst = format.StartsWith("dd", StringComparison.Ordinal) || format.StartsWith("d-", StringComparison.Ordinal) || format.StartsWith("d_", StringComparison.Ordinal) || format.StartsWith("d.", StringComparison.Ordinal);
                var yearFirst = format.StartsWith("yyyy", StringComparison.Ordinal);
                var monthFirst = format.StartsWith("M", StringComparison.Ordinal);
                var knownUsArchive = KnownShowCatalog.UsesUsArchiveDateOrder(collectionName);
                var monthFirstButUnambiguous = monthFirst && date.Day > 12;
                var confidence = yearFirst || dayFirst || monthFirstButUnambiguous || (knownUsArchive && monthFirst) ? "High" : "Probable";
                return (date, match.Index, match.Length, confidence,
                    $"Recognised numeric date using the {format} filename pattern.", $"Numeric date ({format})");
            }
        }

        return null;
    }

    private static (DateTime Date, int Index, int Length, string Confidence, string Reasoning, string Grammar)?
        TryFindDateFromArchiveContext(string path, string filename, string? collectionName)
    {
        if (!KnownShowCatalog.UsesUsArchiveDateOrder(collectionName))
            return null;

        var yearMatches = ArchiveFolderYearRegex().Matches(ArchivePath.GetDirectoryName(path) ?? string.Empty);
        if (yearMatches.Count == 0)
            return null;

        var yearText = yearMatches[yearMatches.Count - 1].Groups["year"].Value;
        if (!int.TryParse(yearText, out var year) || year is < 1980 or > 2035)
            return null;

        var compact = ArchiveMonthDayRegex().Match(filename);
        if (!compact.Success ||
            !int.TryParse(compact.Groups["month"].Value, out var month) ||
            !int.TryParse(compact.Groups["day"].Value, out var day))
            return null;

        try
        {
            var date = new DateTime(year, month, day);
            return (date, compact.Groups["month"].Index,
                compact.Groups["day"].Index + compact.Groups["day"].Length - compact.Groups["month"].Index,
                "High",
                $"Combined the archive folder year {year} with the compact month/day token in the filename.",
                "Archive folder year + compact MMDD");
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool TryRemoveLeadingSequenceBeforeCompleteDate(string filename, out string remainder, out int sequence)
    {
        sequence = 0;
        remainder = filename;
        var leading = LeadingLooseSequenceRegex().Match(filename);
        if (!leading.Success || !int.TryParse(leading.Groups["sequence"].Value, out sequence))
            return false;

        var after = filename[leading.Length..].TrimStart(' ', '-', '_', '.', ')', ']');
        // Only remove the number automatically when the remainder contains a
        // complete date with a named month and explicit year. Otherwise folder
        // analysis remains responsible for deciding whether it is a sequence.
        if (!NamedMonthDateRegex().IsMatch(after))
        {
            sequence = 0;
            return false;
        }

        remainder = after;
        return true;
    }

    private static bool TryRemoveLeadingSequence(string filename, out string remainder, out int sequence)
    {
        var match = LeadingSequenceRegex().Match(filename);
        if (match.Success && int.TryParse(match.Groups["sequence"].Value, out sequence))
        {
            remainder = filename[match.Length..].TrimStart(' ', '-', '_', '.', ')', ']');
            return true;
        }
        remainder = filename;
        sequence = 0;
        return false;
    }

    private static string? DetectCollection(string text)
    {
        // Specific Ron Bennington series must be checked before the broader
        // Bennington alias, otherwise interview archives collapse into the
        // daily Bennington collection.
        if (RonBenningtonInterviewsCollectionRegex().IsMatch(text) || RbiCollectionRegex().IsMatch(text))
            return KnownShowCatalog.RonBenningtonInterviews;
        if (RonRonCollectionRegex().IsMatch(text))
            return KnownShowCatalog.RonRon;
        if (UnmaskedCollectionRegex().IsMatch(text))
            return KnownShowCatalog.Unmasked;
        if (text.Contains("bennington") || text.Contains("benningtoon") || text.Contains("benningotn"))
            return KnownShowCatalog.Bennington;
        if (text.Contains("opie and anthony") || text.Contains("opie & anthony") || text.Contains("o&a") || text.Contains("oanda"))
            return KnownShowCatalog.OpieAnthony;
        if (text.Contains("ron and fez") || text.Contains("ron & fez") || text.Contains("ronfez") ||
            Regex.IsMatch(text, @"(^|[^a-z])ron[\s._-]*(?:and|&)[\s._-]*fez([^a-z]|$)") ||
            Regex.IsMatch(text, @"(^|[^a-z])(?:r&?f|raf)([^a-z]|$)"))
            return KnownShowCatalog.RonFez;
        return null;
    }

    private static (string? Value, string Confidence, string Reasoning) ExtractHeadline(string value, string? collectionName, string? broadcastSlot)
    {
        if (KnownShowCatalog.SupportsUndatedCatalogueItems(collectionName))
        {
            var catalogue = ExtractCatalogueHeadline(value, collectionName);
            if (!string.IsNullOrWhiteSpace(catalogue.Value))
                return catalogue;
        }

        var original = value;
        var expandedAbbreviations = new List<string>();
        value = ExpandArchiveAbbreviations(value, expandedAbbreviations);
        value = DateRegex().Replace(value, " ");
        value = NamedMonthDateRegex().Replace(value, " ");
        value = WeekdayRegex().Replace(value, " ");
        value = CommercialFreeRegex().Replace(value, " ");
        value = EditionRegex().Replace(value, " ");
        value = BestOfTextRegex().Replace(value, " ");
        value = BroadcastSlotRegex().Replace(value, " ");
        value = CollectionTextRegex().Replace(value, " ");
        value = StationDictionary.RemoveRecognisedStationText(value);
        value = SegmentTextRegex().Replace(value, " ");
        value = TechnicalTextRegex().Replace(value, " ");
        value = CopyNoiseRegex().Replace(value, " ");
        value = ArchiveCodeRegex().Replace(value, " ");
        value = ResidualTechnicalQualifierRegex().Replace(value, " ");
        value = Regex.Replace(value, @"(?<=\p{L})[.](?=\p{L})", " ");
        value = Regex.Replace(value, @"[_|]+", " ");
        value = Regex.Replace(value, @"\s*[-–—]+\s*", " ");
        value = Regex.Replace(value, @"\s+", " ").Trim(' ', '-', '_', '.', '|');

        if (string.IsNullOrWhiteSpace(value))
        {
            var structured = string.IsNullOrWhiteSpace(broadcastSlot) ? "" : $" The filename identified this as the {broadcastSlot}.";
            return (null, "None", "The filename contained only the show, date, weekday, slot, segment markers or technical information." + structured);
        }

        if (!TitleQualityService.IsMeaningful(value, collectionName, original))
            return (null, "Low", $"The remaining text ‘{value}’ looked generic or technical, so it was not imported as a headline.");

        var letters = value.Count(char.IsLetter);
        if (letters < 4)
            return (null, "Low", "Too little descriptive text remained after structured and technical tokens were removed.");

        var wordCount = value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
        var confidence = wordCount >= 3 && letters >= 10 ? "High" : "Probable";
        var reasoning = confidence == "High"
            ? "A clear descriptive phrase remained after the show, date, weekday, broadcast slot, part markers and technical tokens were removed."
            : "A short descriptive phrase remained, but it may need review before being used as a headline.";
        if (expandedAbbreviations.Count > 0)
            reasoning += " Recognised archive abbreviation" + (expandedAbbreviations.Count == 1 ? "" : "s") + ": " + string.Join(", ", expandedAbbreviations) + ".";

        return (ToDisplayHeadline(value), confidence, reasoning);
    }

    private static (string? Value, string Confidence, string Reasoning) ExtractCatalogueHeadline(
        string value,
        string? collectionName)
    {
        var original = value;
        var hasMonthYearClue = CatalogueMonthYearRegex().IsMatch(value);
        var yearMatches = hasMonthYearClue ? Array.Empty<Match>() : StandaloneYearRegex().Matches(value).Cast<Match>().ToArray();
        var year = yearMatches.Length > 0 ? yearMatches[^1].Value : string.Empty;

        value = CollectionTextRegex().Replace(value, " ");
        value = CataloguePrefixRegex().Replace(value, " ");
        value = CatalogueCopySuffixRegex().Replace(value, " ");
        if (!hasMonthYearClue)
            value = StandaloneYearRegex().Replace(value, " ");
        value = SegmentTextRegex().Replace(value, " ");
        value = TechnicalTextRegex().Replace(value, " ");
        value = CopyNoiseRegex().Replace(value, " ");
        value = ResidualTechnicalQualifierRegex().Replace(value, " ");
        value = Regex.Replace(value, @"\(\s*\)|\[\s*\]", " ");
        value = Regex.Replace(value, @"[_|]+", " ");
        value = Regex.Replace(value, @"\s*[-–—]+\s*", " ");
        value = Regex.Replace(value, @"\s+", " ").Trim(' ', '-', '_', '.', '|');

        if (string.IsNullOrWhiteSpace(value) ||
            !TitleQualityService.IsMeaningful(value, collectionName, original))
            return (null, "None", "No usable interview, guest or segment label remained after the catalogue prefix was removed.");

        var display = ToCatalogueDisplayHeadline(value);
        if (!string.IsNullOrWhiteSpace(year))
            display += $" ({year})";

        return (display, "High",
            "Recognised an interview or segment catalogue filename. The guest or descriptive label is usable even though a full broadcast date is not present.");
    }

    private static string ToCatalogueDisplayHeadline(string value)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim();
        var letters = value.Where(char.IsLetter).ToArray();
        if (letters.Length > 0 && letters.All(char.IsLower))
            value = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture));

        return Regex.Replace(value, @"\b(?:Snl|Nfl|Wk|Bj)\b", match => match.Value.ToUpperInvariant());
    }

    public static bool IsStructuralOnlyText(string? value, string? collectionName = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value;
        text = DateRegex().Replace(text, " ");
        text = NamedMonthDateRegex().Replace(text, " ");
        text = WeekdayRegex().Replace(text, " ");
        text = CommercialFreeRegex().Replace(text, " ");
        text = EditionRegex().Replace(text, " ");
        text = BestOfTextRegex().Replace(text, " ");
        text = BroadcastSlotRegex().Replace(text, " ");
        text = CollectionTextRegex().Replace(text, " ");
        text = StationDictionary.RemoveRecognisedStationText(text);
        text = SegmentTextRegex().Replace(text, " ");
        text = TechnicalTextRegex().Replace(text, " ");
        text = CopyNoiseRegex().Replace(text, " ");
        text = ArchiveCodeRegex().Replace(text, " ");
        text = ResidualTechnicalQualifierRegex().Replace(text, " ");
        text = Regex.Replace(text, @"(?<=\p{L})[.](?=\p{L})", " ");
        text = Regex.Replace(text, @"[_|]+", " ");
        text = Regex.Replace(text, @"\s*[-–—]+\s*", " ");
        text = Regex.Replace(text, @"\s+", " ").Trim(' ', '-', '_', '.', '|', '(', ')', '[', ']');
        return string.IsNullOrWhiteSpace(text) ||
               !TitleQualityService.IsMeaningful(text, collectionName, value);
    }


    public static bool IsRedundantDateHeadline(string? value, DateTime? broadcastDate)
    {
        if (string.IsNullOrWhiteSpace(value)) return true;
        var text = value.Trim();
        var parsed = TryFindDate(text, null);
        if (parsed is null) return false;

        var remainder = text.Remove(parsed.Value.Index, parsed.Value.Length);
        remainder = WeekdayRegex().Replace(remainder, " ");
        remainder = Regex.Replace(remainder, @"[\s._,;:|()\[\]\-–—]+", " ").Trim();
        if (!string.IsNullOrWhiteSpace(remainder)) return false;

        return !broadcastDate.HasValue || parsed.Value.Date.Date == broadcastDate.Value.Date;
    }

    private static string? DetectBroadcastType(string value) => BestOfTextRegex().IsMatch(value) ? "Best Of" : null;

    private static (string? Value, string Reasoning) DetectEdition(string value)
    {
        var match = EditionRegex().Match(value);
        if (!match.Success) return (null, "No edition marker was found.");
        var token = Regex.Replace(match.Value, @"[()\[\]]", " ");
        token = Regex.Replace(token, @"[\s._-]+", " ").Trim();
        if (Regex.IsMatch(token, @"opie\s*radio", RegexOptions.IgnoreCase))
            return (null, "Recognised OpieRadio as a separate same-day broadcast slot rather than a recording edition.");
        if (Regex.IsMatch(token, @"uncensored", RegexOptions.IgnoreCase))
            return ("Uncensored", "Recognised an uncensored-edition marker in the filename.");
        if (Regex.IsMatch(token, @"no\s*commercial|commercial\s*free", RegexOptions.IgnoreCase))
            return ("Commercial-free", "Recognised a commercial-free recording marker in the filename.");
        if (Regex.IsMatch(token, @"encore", RegexOptions.IgnoreCase))
            return ("Encore", "Recognised an encore-edition marker in the filename.");
        if (Regex.IsMatch(token, @"replay|rebroadcast", RegexOptions.IgnoreCase))
            return ("Replay", "Recognised a replay or rebroadcast marker in the filename.");
        return (ToDisplayHeadline(token), "Recognised an edition marker in the filename.");
    }

    private static (int Score, string Reasoning) CalculateMetadataConfidence(ParsedFilename parsed)
    {
        var score = 0;
        var reasons = new List<string>();
        if (!string.IsNullOrWhiteSpace(parsed.CollectionName)) { score += 30; reasons.Add("show recognised"); }
        if (parsed.AirDate.HasValue)
        {
            var datePoints = parsed.DateConfidence == "High" ? 55 : parsed.DateConfidence == "Probable" ? 35 : 15;
            score += datePoints; reasons.Add($"date {parsed.DateConfidence.ToLowerInvariant()}");
        }
        if (parsed.PartNumber > 1 || parsed.TotalParts.HasValue) { score += 5; reasons.Add("segment recognised"); }
        if (!string.IsNullOrWhiteSpace(parsed.BroadcastSlot)) { score += 5; reasons.Add("broadcast slot recognised"); }
        if (!string.IsNullOrWhiteSpace(parsed.BroadcastType)) { score += 5; reasons.Add("broadcast type recognised"); }
        if (!string.IsNullOrWhiteSpace(parsed.Edition)) { score += 5; reasons.Add("edition recognised"); }
        if (!string.IsNullOrWhiteSpace(parsed.StationCandidate)) { score += 5; reasons.Add("station recognised"); }
        if (!string.IsNullOrWhiteSpace(parsed.HeadlineCandidate))
        {
            score += parsed.HeadlineConfidence == "High" ? 10 : parsed.HeadlineConfidence == "Probable" ? 5 : 0;
            reasons.Add($"headline {parsed.HeadlineConfidence.ToLowerInvariant()}");
        }
        score = Math.Clamp(score, 0, 100);
        return (score, reasons.Count == 0 ? "No reliable structured metadata was recognised." : string.Join(", ", reasons) + ".");
    }

    private static string? DetectBroadcastSlot(string value)
    {
        if (OpieRadioSlotRegex().IsMatch(value)) return "OpieRadio Edition";
        var match = BroadcastSlotRegex().Match(value);
        if (!match.Success) return null;
        var token = match.Groups["slot"].Value.ToUpperInvariant();
        return token switch
        {
            "MID" or "MIDDAY" => "Midday show",
            "EVE" or "EVENING" => "Evening show",
            "AM" or "MORNING" => "Morning show",
            "PM" or "AFTERNOON" => "Afternoon show",
            "LATE" => "Late show",
            _ => null
        };
    }

    private static string ExpandArchiveAbbreviations(string value, ICollection<string> expanded)
    {
        if (BestOfRegex().IsMatch(value))
        {
            value = BestOfRegex().Replace(value, " Best Of ");
            expanded.Add("BO → Best Of");
        }
        return value;
    }

    private static string ToDisplayHeadline(string value)
    {
        value = Regex.Replace(value, @"\s+", " ").Trim();
        if (value.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            value = CultureInfo.CurrentCulture.TextInfo.ToTitleCase(value.ToLower(CultureInfo.CurrentCulture));
        return value;
    }


    [GeneratedRegex(@"(?<!\d)(?<month>0?[1-9]|1[0-2])[-._ /](?<day>0?[1-9]|[12]\d|3[01])[-._ /](?<year>\d{2})(?!\d)")]
    private static partial Regex TwoDigitUsDateRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}[-._ /](?:0?[1-9]|1[0-2])[-._ /](?:0?[1-9]|[12]\d|3[01])(?!\d)|(?<!\d)(?:19|20)\d{6}(?!\d)|(?<!\d)(?:0?[1-9]|1[0-2])[-._ /](?:0?[1-9]|[12]\d|3[01])[-._ /](?:19|20)\d{2}(?!\d)|(?<!\d)(?:0?[1-9]|[12]\d|3[01])[-._ /](?:0?[1-9]|1[0-2])[-._ /](?:19|20)\d{2}(?!\d)")]
    private static partial Regex DateRegex();

    [GeneratedRegex(@"\b(?:0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?[\s._,-]+(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)[\s._,-]+(?:19|20)?\d{2}\b|\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)[\s._,-]+(?:0?[1-9]|[12]\d|3[01])(?:st|nd|rd|th)?(?:,)?[\s._,-]+(?:19|20)?\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex NamedMonthDateRegex();

    [GeneratedRegex(@"\b(\d{1,2})(?:st|nd|rd|th)\b", RegexOptions.IgnoreCase)]
    private static partial Regex OrdinalSuffixRegex();

    [GeneratedRegex(@"^(?:\[|\()?\s*(?<sequence>\d{1,5})\s*(?:\]|\)|[-._])\s*")]
    private static partial Regex LeadingSequenceRegex();

    [GeneratedRegex(@"^(?:\[|\()?\s*(?<sequence>\d{1,5})\s*(?:\]|\)|[-._]|\s+)\s*")]
    private static partial Regex LeadingLooseSequenceRegex();

    [GeneratedRegex(@"\b(?:mon(?:day)?|tue(?:s|sday)?|wed(?:nesday)?|thu(?:rs|rsday)?|fri(?:day)?|sat(?:urday)?|sun(?:day)?|tthu)\b", RegexOptions.IgnoreCase)]
    private static partial Regex WeekdayRegex();

    [GeneratedRegex(@"(?:^|[\s_\-.()\[\]])(?<slot>MID|MIDDAY|EVE|EVENING|AM|MORNING|PM|AFTERNOON|LATE)(?:$|[\s_\-.()\[\]])", RegexOptions.IgnoreCase)]
    private static partial Regex BroadcastSlotRegex();

    [GeneratedRegex(@"(?:^|[\s._\-()\[\]])opie[\s._-]*radio(?:[\s._-]*edition)?(?:$|[\s._\-()\[\]])", RegexOptions.IgnoreCase)]
    private static partial Regex OpieRadioSlotRegex();

    [GeneratedRegex(@"\b(?:rbi|ron[\s._-]*bennington[\s._-]*interviews?|(?:(?:the[\s._-]+)?ron[\s._-]*(?:and|&)[\s._-]*ron(?:[\s._-]+show)?|ronronshow)|(?:unmasked|unmaked)(?:[\s._-]+with[\s._-]+ron[\s._-]+bennington)?|(?:the[\s._-]+)?(?:ron[\s._-]*(?:and|&)[\s._-]*fez|ron[\s._-]+fez|ronfez|bennington|benningtoon|benningotn|opie[\s._-]*(?:and|&)[\s._-]*anthony|o[\s._-]*&?[\s._-]*a|r[\s._-]*&?[\s._-]*f|raf))\b", RegexOptions.IgnoreCase)]
    private static partial Regex CollectionTextRegex();

    [GeneratedRegex(@"\bron[\s._-]*bennington[\s._-]*interviews?\b", RegexOptions.IgnoreCase)]
    private static partial Regex RonBenningtonInterviewsCollectionRegex();

    [GeneratedRegex(@"(?:^|[\\/])rbi(?=$|[\\/\s._-])", RegexOptions.IgnoreCase)]
    private static partial Regex RbiCollectionRegex();

    [GeneratedRegex(@"^(?:rbi|unmasked|unmaked|town[\s._-]*hall)[\s._-]+", RegexOptions.IgnoreCase)]
    private static partial Regex CataloguePrefixRegex();

    [GeneratedRegex(@"(?:[\s._-]+|\()\d{1,2}\)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogueCopySuffixRegex();

    [GeneratedRegex(@"(?<!\d)(?:19|20)\d{2}(?!\d)")]
    private static partial Regex StandaloneYearRegex();

    [GeneratedRegex(@"\b(?:jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|jun(?:e)?|jul(?:y)?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?)[\s,._-]+(?:19|20)\d{2}\b", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogueMonthYearRegex();

    [GeneratedRegex(@"\b(?:(?:the[\s._-]+)?ron[\s._-]*(?:and|&)[\s._-]*ron(?:[\s._-]+show)?|ronronshow)\b", RegexOptions.IgnoreCase)]
    private static partial Regex RonRonCollectionRegex();

    [GeneratedRegex(@"(?:^|[\\/])(?:siriusxm[\s._-]+)?(?:unmasked|unmaked)(?:[\s._-]+with[\s._-]+ron[\s._-]+bennington)?(?=$|[\\/\s._-])", RegexOptions.IgnoreCase)]
    private static partial Regex UnmaskedCollectionRegex();

    [GeneratedRegex(@"\b(?:part|pt|segment|seg|hour|hr|disc|disk|cd)?\s*\d{1,2}\s*(?:of|/)\s*\d{1,2}\b|\b(?:hour|hr|part|pt|segment|seg|disc|disk|cd)\s*[0-9a-z]{1,2}\b|(?:^|[\s_\-.()\[\]])[ab](?:$|[\s_\-.()\[\]])", RegexOptions.IgnoreCase)]
    private static partial Regex SegmentTextRegex();

    [GeneratedRegex(@"\b(?:cf\s*\d{2,3}\s*k(?:bps)?|cf\d{2,3}k|\d{2,3}\s*k(?:bps)?|\d{2,3}\s*kbps|mp3|m4a|aac|flac|ogg|wav|wma|stereo|mono|vbr|cbr|hq|high\s*quality|low\s*quality|remaster(?:ed)?|re-?encode(?:d)?|rip(?:ped)?|web\s*rip)\b", RegexOptions.IgnoreCase)]
    private static partial Regex TechnicalTextRegex();

    [GeneratedRegex(@"\b(?:episode|regular\s+episode|full\s+show|radio\s+show|archive|download(?:ed)?|copy|new|final|fixed|complete)\b|\(\d+\)$", RegexOptions.IgnoreCase)]
    private static partial Regex CopyNoiseRegex();

    [GeneratedRegex(@"(?:^|[\s_\-.\[(])BO(?:$|[\s_\-.\])])", RegexOptions.IgnoreCase)]
    private static partial Regex BestOfRegex();

    [GeneratedRegex(@"(?:^|[\s_\-.\[(])(?:BO|BEST[\s_\-.]*OF)(?:$|[\s_\-.\])])", RegexOptions.IgnoreCase)]
    private static partial Regex BestOfTextRegex();

    [GeneratedRegex(@"(?<prefix>\b(?:raf|r&f|rf))[\s._-]*(?<month>0[1-9]|1[0-2])(?<day>0[1-9]|[12]\d|3[01])[\s._-]*(?<year>\d{2})(?=$|[\s._-])", RegexOptions.IgnoreCase)]
    private static partial Regex RonFezCompactDateRegex();

    [GeneratedRegex(@"\b(?:no\s*commercials?|commercial[\s_-]*free|cf)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CommercialFreeRegex();

    [GeneratedRegex(@"(?:^|[\s._-])(?:raf|r&f|rf)(?<month>0[1-9]|1[0-2])(?<day>0[1-9]|[12]\d|3[01])(?=$|[\s._-])", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveMonthDayRegex();

    [GeneratedRegex(@"(?:^|[\\/._ -])(?<year>(?:19|20)\d{2})(?=$|[\\/._ -])")]
    private static partial Regex ArchiveFolderYearRegex();

    [GeneratedRegex(@"\b(?:raf|r&f|rf)\d{3,6}\b", RegexOptions.IgnoreCase)]
    private static partial Regex ArchiveCodeRegex();

    [GeneratedRegex(@"\b(?:partial|take[\s._-]*\d+|copy[\s._-]*\d+|ph)\b", RegexOptions.IgnoreCase)]
    private static partial Regex ResidualTechnicalQualifierRegex();

    [GeneratedRegex(@"(?:\(|\[)?\b(?:opie[\s._-]*radio(?:[\s._-]*edition)?|uncensored(?:[\s._-]*edition)?|no[\s._-]*commercials?|commercial[\s._-]*free|encore(?:[\s._-]*edition)?|replay|rebroadcast)\b(?:\)|\])?", RegexOptions.IgnoreCase)]
    private static partial Regex EditionRegex();
}
