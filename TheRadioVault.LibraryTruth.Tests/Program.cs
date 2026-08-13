using TheRadioVault.Core.LibraryTruth;
using TheRadioVault.Core.Services;
using TheRadioVault.Data.Database;
using TheRadioVault.Services.Services;

var tests = new (string Name, Action Run)[]
{
    ("Parser accepts variable-width US dates", ParserAcceptsVariableWidthUsDates),
    ("Parser recognises Roman multipart suffixes", ParserRecognisesRomanMultipartSuffixes),
    ("PM and evening slots reconcile", PmAndEveningSlotsReconcile),
    ("OpieRadio is a separate broadcast slot", OpieRadioIsSeparateBroadcastSlot),
    ("Explicit filename show overrides folder", ExplicitFilenameShowOverridesFolder),
    ("Library Truth recovers AFRO short dates from folder context", LibraryTruthRecoversAfroShortDate),
    ("Library Truth recognises Bennington OR slot", LibraryTruthRecognisesBenningtonOr),
    ("Library Truth keeps parent show for AFRO format broadcasts", LibraryTruthKeepsParentShowForAfro),
    ("Library Truth recovers compact RaF month day from year folder", LibraryTruthRecoversCompactRafMonthDay),
    ("Library Truth learns year from labelled year folders", LibraryTruthLearnsYearFromLabelledFolder),
    ("Library Truth parses indexed named-month dates", LibraryTruthParsesIndexedNamedMonthDates),
    ("Library Truth does not confuse source indices with years", LibraryTruthDoesNotConfuseSourceIndexWithYear),
    ("Library Truth distinguishes explicit and ambiguous multipart markers", LibraryTruthRecognisesAdditionalMultipartMarkers),
    ("Library Truth protects leading track and lone version numbers", LibraryTruthProtectsTrackAndVersionNumbers),
    ("Library Truth keeps variant and source filename families separate", LibraryTruthKeepsRecordingFamiliesSeparate),
    ("Library Truth normalises safe multipart family suffixes", LibraryTruthNormalizesSafeMultipartFamilySuffixes),
    ("Library Truth reassembles annotated multipart families", LibraryTruthReassemblesAnnotatedMultipartFamilies),
    ("Library Truth assembles Roman multipart files into one broadcast", LibraryTruthGroupsRomanMultipart),
    ("Library Truth preserves genuinely unknown dates", LibraryTruthPreservesUnknownDate),
    ("Library Truth shadow index separates broadcasts recordings and files", LibraryTruthShadowIndexSeparatesLayers),
    ("Library Truth treats alternate recordings as normal structure", LibraryTruthTreatsAlternateRecordingsAsNormal),
    ("Library Truth keeps identical audio with conflicting dates separate", LibraryTruthKeepsConflictingDatesSeparate),
    ("Library Truth groups exact unknown physical copies", LibraryTruthGroupsExactUnknownCopies),
    ("Library Truth separates full captures from multipart assemblies", LibraryTruthSeparatesFullAndMultipartRecordings),
    ("Library Truth classifies truncated recordings and ranks a preferred capture", LibraryTruthClassifiesTruncatedRecording),
    ("Library Truth compares multipart coverage with a full capture", LibraryTruthComparesMultipartCoverage),
    ("Library Truth flags suspicious substantial merges", LibraryTruthFlagsSuspiciousMerge),
    ("Library Truth propagates strong cross-date audio conflicts", LibraryTruthPropagatesStrongCrossDateConflict),
    ("Library Truth produces adoption and year audit summaries", LibraryTruthProducesAdoptionAudit),
    ("Library Truth assembles variant multipart families without cross-pairing", LibraryTruthAssemblesVariantFamiliesSafely),
    ("Library Truth promotes bare multipart numbers only with sibling evidence", LibraryTruthPromotesBareMultipartSequence),
    ("Library Truth flags repeated programme-specific duration clusters", LibraryTruthFlagsProgrammeSpecificDurationClusters),
    ("Library Truth detects composite AM and PM coverage", LibraryTruthDetectsCompositeCoverage),
    ("Library Truth detects same-date cross-slot equivalents", LibraryTruthDetectsCrossSlotEquivalent),
    ("Library Truth persists direct recording segment coverage", LibraryTruthPersistsDirectSegmentCoverage),
    ("Library Truth prepares guarded adoption plans without live writes", LibraryTruthPreparesGuardedAdoptionPreview),
    ("Library Truth rehearses adoption on a disposable clone and verifies rollback", LibraryTruthRehearsalRollsBackDisposableClone),
    ("Library Truth guarded adoption commits only the verified plan", LibraryTruthGuardedAdoptionCommitsVerifiedPlan),
    ("Library Truth classifies metadata conflicts and preserves alternates", LibraryTruthClassifiesMetadataConflicts),
    ("Library Truth refines generated metadata policies", LibraryTruthRefinesGeneratedMetadataPolicies),
};

var selectedTests = args.Length == 0
    ? tests
    : tests.Where(test => args.Any(filter => test.Name.Contains(filter, StringComparison.OrdinalIgnoreCase))).ToArray();
if (selectedTests.Length == 0)
{
    Console.Error.WriteLine("No Library Truth tests matched the supplied filters.");
    return 2;
}

var failures = new List<string>();
foreach (var test in selectedTests)
{
    try
    {
        test.Run();
        Console.WriteLine($"PASS  {test.Name}");
    }
    catch (Exception exception)
    {
        failures.Add($"{test.Name}: {exception.Message}");
        Console.Error.WriteLine($"FAIL  {test.Name}: {exception.Message}");
    }
}

Console.WriteLine($"{selectedTests.Length - failures.Count}/{selectedTests.Length} Library Truth tests passed.");
return failures.Count == 0 ? 0 : 1;

static void ParserAcceptsVariableWidthUsDates()
{
    var parser = new FilenameParserService();
    var bennington = parser.Parse(@"E:\Radio\Bennington 2-6-2015.m4a");
    Equal(new DateTime(2015, 2, 6), bennington.AirDate!.Value);
    Equal("High", bennington.DateConfidence);

    var ronFez = parser.Parse(@"E:\Radio\Ron.And.Fez.9-04-2014.CF64K.m4a");
    Equal(new DateTime(2014, 9, 4), ronFez.AirDate!.Value);
    Equal("High", ronFez.DateConfidence);
}

static void ParserRecognisesRomanMultipartSuffixes()
{
    var parser = new FilenameParserService();
    var partOne = parser.Parse(@"E:\Radio\R&F-2015-02-27 I.mp3");
    var partTwo = parser.Parse(@"E:\Radio\R&F-2015-02-27 II.mp3");
    Equal(1, partOne.PartNumber);
    Equal(2, partTwo.PartNumber);
    True(partOne.MultipartKind == "Part", $"Part I kind was '{partOne.MultipartKind}'.");
    True(partTwo.MultipartKind == "Part", $"Part II kind was '{partTwo.MultipartKind}'.");
    True(string.IsNullOrWhiteSpace(partOne.HeadlineCandidate), $"Part I headline was '{partOne.HeadlineCandidate}'.");
    True(string.IsNullOrWhiteSpace(partTwo.HeadlineCandidate), $"Part II headline was '{partTwo.HeadlineCandidate}'.");
}

static void PmAndEveningSlotsReconcile()
{
    True(BroadcastSlotNormalizer.Equivalent("PM", "Evening show"));
    True(BroadcastSlotNormalizer.Equivalent("Afternoon show", "Evening show"));
    True(BroadcastSlotNormalizer.Equivalent("12:00 p.m.–3:00 p.m. Eastern", "PM"));
    True(!BroadcastSlotNormalizer.Equivalent("Morning show", "Evening show"));
}

static void OpieRadioIsSeparateBroadcastSlot()
{
    var parsed = new FilenameParserService().Parse(@"E:\Bennington\Bennington - 2015-05-29 Fri (OpieRadio Edition).m4a");
    Equal("OpieRadio Edition", parsed.BroadcastSlot);
    True(string.IsNullOrWhiteSpace(parsed.Edition));
    True(string.IsNullOrWhiteSpace(parsed.HeadlineCandidate));
    Equal("BENNINGTON-2015-05-29-OPIERADIO-EDITION", BroadcastIdentityService.CreateStableId("Bennington", new DateOnly(2015, 5, 29), 1, parsed.BroadcastSlot));
}

static void ExplicitFilenameShowOverridesFolder()
{
    var parsed = new FilenameParserService().Parse(@"E:\Ron & Fez Archive\2007-10-11-O&A-CF64k.m4a");
    Equal("Opie & Anthony", parsed.CollectionName);
    True(parsed.CollectionDetectedFromFilename);
}


static void LibraryTruthRecoversAfroShortDate()
{
    var parser = new LibraryTruthParser();
    var input = TruthInput(@"D:\radio_shows\AFRO Shows\2004\Afro Show 12-28-04.mp3", "AFRO Show");
    var context = new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        LibraryRoot = @"D:\radio_shows\AFRO Shows",
        AssignedCollectionName = "AFRO Show",
        DominantCollectionName = "AFRO Show",
        YearHint = 2004,
        DateOrder = "US",
        FileCount = 10
    };
    var parsed = parser.Parse(input, context);
    Equal(new DateOnly(2004, 12, 28), parsed.AirDate!.Value);
    Equal("AFRO Show", parsed.CollectionName);
}

static void LibraryTruthRecognisesBenningtonOr()
{
    var input = TruthInput(@"D:\radio_shows\Bennington\2015-04-24 Bennington OR 64k.m4a", "Bennington");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Bennington",
        DominantCollectionName = "Bennington",
        DateOrder = "US",
        FileCount = 50
    });
    Equal("OpieRadio Edition", parsed.BroadcastSlot);
    True(parsed.CanonicalBroadcastKey.Contains("OPIERADIO", StringComparison.Ordinal));
}

static void LibraryTruthKeepsParentShowForAfro()
{
    var input = TruthInput(@"D:\Radio\RonFez\Ron and Fez Mini AFRO Show 1-06-05.mp3", "Ron & Fez");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    });
    Equal("Ron & Fez", parsed.CollectionName);
    Equal(new DateOnly(2005, 1, 6), parsed.AirDate);
    True(parsed.Headline.Contains("AFRO", StringComparison.OrdinalIgnoreCase));
    True(parsed.Evidence.Any(x => x.Field == "programme-format" && x.Value == "AFRO Show"));
}

static void LibraryTruthRecoversCompactRafMonthDay()
{
    var input = TruthInput(@"D:\Radio\RonFez\2003\RaF1124-mid-Pt1.mp3", "Ron & Fez");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DominantCollectionName = "Ron & Fez",
        YearHint = 2003,
        DateOrder = "US",
        FileCount = 100
    });
    Equal(new DateOnly(2003, 11, 24), parsed.AirDate);
    Equal("Midday", parsed.BroadcastSlot);
    Equal(1, parsed.PartNumber);
}


static void LibraryTruthLearnsYearFromLabelledFolder()
{
    var input = new LibraryTruthFileInput
    {
        MediaFileId = 301,
        CurrentEpisodeId = 301,
        Path = @"D:\radio_shows\Ron & Fez Archive\Ron & Fez 2003\RaF1003-Part2.mp3",
        OriginalFilename = "RaF1003-Part2.mp3",
        CurrentCollectionName = "Ron & Fez",
        AssignedCollectionName = "Ron & Fez",
        LibraryRoot = @"D:\radio_shows\Ron & Fez Archive",
        CurrentPartNumber = 1
    };
    var contexts = new LibraryTruthContextAnalyzer().Analyse(new[] { input });
    var context = contexts[LibraryTruthContextAnalyzer.ContextKey(input)];
    Equal(2003, context.YearHint ?? -1);
    var parsed = new LibraryTruthParser().Parse(input, context);
    Equal(new DateOnly(2003, 10, 3), parsed.AirDate);
    Equal(2, parsed.PartNumber);
}

static void LibraryTruthParsesIndexedNamedMonthDates()
{
    var parser = new LibraryTruthParser();
    var input = TruthInput(@"D:\Radio\Ron & Fez 2009\36 _1st Oct, 2009(1).m4a", "Ron & Fez");
    var parsed = parser.Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    });
    Equal(new DateOnly(2009, 10, 1), parsed.AirDate);
    True(string.IsNullOrWhiteSpace(parsed.Headline), $"Indexed date headline was '{parsed.Headline}'.");
}

static void LibraryTruthDoesNotConfuseSourceIndexWithYear()
{
    var parser = new LibraryTruthParser();
    var input = TruthInput(@"D:\Radio\Ron & Fez 2010\27 March 16, 2010.m4a", "Ron & Fez");
    var parsed = parser.Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        YearHint = 2010,
        DateOrder = "US",
        FileCount = 100
    });
    Equal(new DateOnly(2010, 3, 16), parsed.AirDate);
    Equal(1, parsed.PartNumber);
}

static void LibraryTruthRecognisesAdditionalMultipartMarkers()
{
    var parser = new LibraryTruthParser();
    var context = new LibraryTruthFolderContext
    {
        ContextKey = @"D:\Radio\RonFez",
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    };

    var compact = parser.Parse(TruthInput(@"D:\Radio\RonFez\Ron and Fez_ 2009-01-08-P2.mp3", "Ron & Fez"), context);
    Equal(2, compact.PartNumber);
    True(string.IsNullOrWhiteSpace(compact.Headline), $"Compact multipart headline was '{compact.Headline}'.");

    var trailing = parser.Parse(TruthInput(@"D:\Radio\RonFez\Ron and Fez_ 12_20_2011.2.mp3", "Ron & Fez"), context);
    Equal(1, trailing.PartNumber);

    var alternateTake = parser.Parse(TruthInput(@"D:\Radio\RonFez\R&F-07-16-2002 - take 2.mp3", "Ron & Fez"), context);
    Equal(1, alternateTake.PartNumber);
}


static void LibraryTruthProtectsTrackAndVersionNumbers()
{
    var parser = new LibraryTruthParser();
    var context = new LibraryTruthFolderContext
    {
        ContextKey = @"D:\Radio\RonFez",
        AssignedCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    };

    var track = parser.Parse(TruthInput(@"D:\Radio\RonFez\09 17th, Jan 2008.m4a", "Ron & Fez"), context);
    Equal(1, track.PartNumber);
    True(string.IsNullOrWhiteSpace(track.MultipartKind));

    var version = parser.Parse(TruthInput(@"D:\Radio\RonFez\20011123 R&F 1.mp3", "Ron & Fez"), context);
    Equal(1, version.PartNumber);
    True(string.IsNullOrWhiteSpace(version.MultipartKind));

    var trackStructure = LibraryTruthRecordingStructure.Analyse("09 17th, Jan 2008.m4a", false);
    Equal(LibraryTruthNumericTokenKind.LeadingTrackNumber, trackStructure.NumericTokenKind);
    var versionStructure = LibraryTruthRecordingStructure.Analyse("20011123 R&F 1.mp3", false);
    Equal(LibraryTruthNumericTokenKind.AmbiguousTrailingNumber, versionStructure.NumericTokenKind);
}

static void LibraryTruthKeepsRecordingFamiliesSeparate()
{
    var v1p1 = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-06-25-V1-P1.mp3", true);
    var v1p2 = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-06-25-V1-P2.mp3", true);
    var v2p1 = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-06-25-V2-P1.mp3", true);
    Equal(v1p1.FamilyKey, v1p2.FamilyKey);
    True(!string.Equals(v1p1.FamilyKey, v2p1.FamilyKey, StringComparison.OrdinalIgnoreCase));

    var shortFamily = LibraryTruthRecordingStructure.Analyse("20010927 R&F - Part 1.mp3", true);
    var longFamily = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2001-09-27 pt.2.mp3", true);
    True(!string.Equals(shortFamily.FamilyKey, longFamily.FamilyKey, StringComparison.OrdinalIgnoreCase));
}

static void LibraryTruthNormalizesSafeMultipartFamilySuffixes()
{
    var attachedA = LibraryTruthRecordingStructure.Analyse("R&F-10-31-2002a.mp3", true);
    var attachedB = LibraryTruthRecordingStructure.Analyse("R&F-10-31-2002b.mp3", true);
    Equal(attachedA.FamilyKey, attachedB.FamilyKey);

    var annotatedPart = LibraryTruthRecordingStructure.Analyse("RaF1020-Part2-ph.mp3", true);
    var plainPart = LibraryTruthRecordingStructure.Analyse("RaF1020-Part3.mp3", true);
    Equal(annotatedPart.FamilyKey, plainPart.FamilyKey);

    var partialPart = LibraryTruthRecordingStructure.Analyse("RaF1110-Part3-partial.mp3", true);
    var siblingPart = LibraryTruthRecordingStructure.Analyse("RaF1110-Part4.mp3", true);
    Equal(partialPart.FamilyKey, siblingPart.FamilyKey);

    var fnrPart = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-08-23-Part1-FNR.mp3", true);
    var standardPart = LibraryTruthRecordingStructure.Analyse("Ron and Fez_ 2002-08-23-Part2.mp3", true);
    True(!string.Equals(fnrPart.FamilyKey, standardPart.FamilyKey, StringComparison.OrdinalIgnoreCase));
    True(fnrPart.ProgrammeTokens.Contains("fnr"));
}

static void LibraryTruthReassemblesAnnotatedMultipartFamilies()
{
    var hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-10-31-2002a.mp3", "2002-10-31", "", 2 * hour, "AB-A"),
        ("R&F-10-31-2002b.mp3", "2002-10-31", "", 1 * hour, "AB-B")
    }, engine =>
    {
        var recordings = engine.GetRecordings();
        if (recordings.Count != 1)
            throw new InvalidOperationException("Expected one PH multipart recording: " +
                string.Join(" | ", recordings.Select(x => $"{x.RecordingKey}; segments={x.SegmentCount}; role={x.Role}")));
        var recording = recordings[0];
        Equal(2, recording.SegmentCount);
        Equal(3 * hour, recording.DurationMs);
        Equal("Complete multipart recording", recording.Role);
    });

    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("Ron and Fez_ 2003-10-20-Part2-ph.mp3", "2003-10-20", "", 20 * 60_000L, "PH-2"),
        ("Ron and Fez_ 2003-10-20-Part3.mp3", "2003-10-20", "", 20 * 60_000L, "PH-3"),
        ("Ron and Fez_ 2003-10-20-Part4.mp3", "2003-10-20", "", 20 * 60_000L, "PH-4"),
        ("Ron and Fez_ 2003-10-20-Part5.mp3", "2003-10-20", "", 20 * 60_000L, "PH-5"),
        ("Ron and Fez_ 2003-10-20-Part6-of-6.mp3", "2003-10-20", "", 20 * 60_000L, "PH-6")
    }, engine =>
    {
        var recordings = engine.GetRecordings();
        if (recordings.Count != 1)
            throw new InvalidOperationException("Expected one PH multipart recording: " +
                string.Join(" | ", recordings.Select(x => $"{x.RecordingKey}; segments={x.SegmentCount}; role={x.Role}")));
        var recording = recordings[0];
        Equal(5, recording.SegmentCount);
        Equal("Incomplete multipart recording", recording.Role);
    });

    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("Ron and Fez_ 2003-11-10-Part1.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-1"),
        ("Ron and Fez_ 2003-11-10-Part2.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-2"),
        ("Ron and Fez_ 2003-11-10-Part3-partial.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-3"),
        ("Ron and Fez_ 2003-11-10-Part4-of-4.mp3", "2003-11-10", "", 18 * 60_000L, "PARTIAL-4")
    }, engine =>
    {
        var recording = engine.GetRecordings().Single();
        Equal(4, recording.SegmentCount);
        Equal("Complete multipart recording", recording.Role);
    });
}

static void LibraryTruthGroupsRomanMultipart()
{
    var parser = new LibraryTruthParser();
    var first = TruthInput(@"D:\radio_shows\Ron & Fez Archive\R&F-2015-02-27 I.mp3", "Ron & Fez", 1);
    var second = TruthInput(@"D:\radio_shows\Ron & Fez Archive\R&F-2015-02-27 II.mp3", "Ron & Fez", 2);
    var context = new LibraryTruthFolderContext { ContextKey = first.DirectoryPath, AssignedCollectionName = "Ron & Fez", DominantCollectionName = "Ron & Fez", DateOrder = "US", FileCount = 100 };
    var parsedFirst = parser.Parse(first, context);
    var parsedSecond = parser.Parse(second, context);
    Equal(1, parsedFirst.PartNumber);
    Equal(2, parsedSecond.PartNumber);
    Equal(parsedFirst.CanonicalBroadcastKey, parsedSecond.CanonicalBroadcastKey);
}

static void LibraryTruthPreservesUnknownDate()
{
    var input = TruthInput(@"D:\radio_shows\Ron & Fez Archive\Ron & Zero Fez Thunderdome.mp3", "Ron & Fez");
    var parsed = new LibraryTruthParser().Parse(input, new LibraryTruthFolderContext
    {
        ContextKey = input.DirectoryPath,
        AssignedCollectionName = "Ron & Fez",
        DominantCollectionName = "Ron & Fez",
        DateOrder = "US",
        FileCount = 100
    });
    True(parsed.AirDate is null);
    True(parsed.Warnings.Any(x => x.Code == "unknown-date"));
}

static LibraryTruthFileInput TruthInput(string path, string assignedCollection, long id = 1)
    => new()
    {
        MediaFileId = id,
        CurrentEpisodeId = id,
        Path = path,
        OriginalFilename = Path.GetFileName(path),
        CurrentCollectionName = assignedCollection,
        AssignedCollectionName = assignedCollection,
        LibraryRoot = Path.GetDirectoryName(path) ?? string.Empty,
        CurrentPartNumber = 1
    };

static void LibraryTruthGroupsExactUnknownCopies()
{
    var parser = new LibraryTruthParser();
    // LibraryTruthFileInput is a class, so use explicit objects to carry identical full hashes.
    var inputA = new LibraryTruthFileInput
    {
        MediaFileId = 201,
        CurrentEpisodeId = 201,
        Path = @"D:\Radio\Mystery\unknown-a.mp3",
        OriginalFilename = "unknown-a.mp3",
        FullHash = "ABCDEF0123456789",
        AssignedCollectionName = "Ron & Fez",
        CurrentCollectionName = "Ron & Fez",
        CurrentPartNumber = 1
    };
    var inputB = new LibraryTruthFileInput
    {
        MediaFileId = 202,
        CurrentEpisodeId = 202,
        Path = @"D:\Radio\Mystery\unknown-b.mp3",
        OriginalFilename = "unknown-b.mp3",
        FullHash = "ABCDEF0123456789",
        AssignedCollectionName = "Ron & Fez",
        CurrentCollectionName = "Ron & Fez",
        CurrentPartNumber = 1
    };
    var context = new LibraryTruthFolderContext { ContextKey = inputA.DirectoryPath, AssignedCollectionName = "Ron & Fez", DateOrder = "US", FileCount = 2 };
    var parsedA = parser.Parse(inputA, context);
    var parsedB = parser.Parse(inputB, context);
    Equal(parsedA.CanonicalBroadcastKey, parsedB.CanonicalBroadcastKey);
    True(parsedA.AirDate is null && parsedB.AirDate is null);
}

static void LibraryTruthShadowIndexSeparatesLayers()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,part_number,total_parts,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2015-02-27','High','Part I','Unplayed',$now,$now,'',1,NULL,'CURRENT-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,part_number,total_parts,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2015-02-27','High','Part II','Unplayed',$now,$now,'',1,NULL,'CURRENT-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CURRENT-A'),'D:\Radio\RonFez\Ron and Fez 2-27-2015 I.mp3','Ron and Fez 2-27-2015 I.mp3',1000,$now,0,$now,3600000,'PART-A','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CURRENT-B'),'D:\Radio\RonFez\Ron and Fez 2-27-2015 II.mp3','Ron and Fez 2-27-2015 II.mp3',1100,$now,0,$now,3500000,'PART-B','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        var run = engine.BuildShadowIndex().Summary;
        Equal(2, run.PhysicalFiles);
        Equal(1, run.ProposedBroadcasts);
        Equal(1, run.MultipartBroadcasts);
        Equal(1, run.MergeGroups);
        var recordings = engine.GetRecordings();
        Equal(1, recordings.Count);
        Equal(2, recordings[0].SegmentCount);
        Equal(2, recordings[0].FileCount);
        var files = engine.GetFiles();
        Equal(2, files.Count);
        True(files.All(x => !string.IsNullOrWhiteSpace(x.RecordingKey)));
        True(files.All(x => x.Evidence.Contains("show:", StringComparison.OrdinalIgnoreCase)));
        True(files.All(x => x.Warnings == "No warnings."));
        Equal(1, files.Select(x => x.RecordingKey).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void LibraryTruthTreatsAlternateRecordingsAsNormal()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-alternates.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-08','High','Capture A','Unplayed',$now,$now,'ALT-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-08','High','Capture B','Unplayed',$now,$now,'ALT-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALT-A'),'D:\Radio\RonFez\20010808 R&F.mp3','20010808 R&F.mp3',1000,$now,0,$now,3600000,'PARTIAL-A','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALT-B'),'D:\Radio\RonFez\Ron and Fez_ 2001-08-08.mp3','Ron and Fez_ 2001-08-08.mp3',1100,$now,0,$now,3599000,'PARTIAL-B','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        var summary = engine.BuildShadowIndex().Summary;
        Equal(1, summary.ProposedBroadcasts);
        Equal(0, summary.NeedsReview);
        Equal(1, summary.MergeGroups);
        var broadcasts = engine.GetBroadcasts();
        Equal("Proposed changes", broadcasts.Single().Status);
        Equal(2, broadcasts.Single().RecordingCount);
        Equal(0, engine.GetFiles("needs-attention").Count);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthKeepsConflictingDatesSeparate()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-conflict.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2002-04-24','High','First claim','Unplayed',$now,$now,'CLAIM-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-04-24','High','Second claim','Unplayed',$now,$now,'CLAIM-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,full_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-A'),'D:\Radio\RonFez\R&F 04-24-2002.mp3','R&F 04-24-2002.mp3',1000,$now,0,$now,3600000,'PARTIAL-SAME','FULL-SAME','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,full_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-B'),'D:\Radio\RonFez\R&F 04-24-2001.mp3','R&F 04-24-2001.mp3',1000,$now,0,$now,3600000,'PARTIAL-SAME','FULL-SAME','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        var summary = engine.BuildShadowIndex().Summary;
        Equal(2, summary.ProposedBroadcasts);
        Equal(2, summary.NeedsReview);
        var files = engine.GetFiles("needs-attention");
        Equal(2, files.Count);
        True(files.All(x => x.Warnings.Contains("conflicting", StringComparison.OrdinalIgnoreCase)));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthSeparatesFullAndMultipartRecordings()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-full-multipart.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-23','High','Full','Unplayed',$now,$now,'FULL');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-23','High','Part 1','Unplayed',$now,$now,'P1');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-23','High','Part 2','Unplayed',$now,$now,'P2');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='FULL'),'D:\Radio\RonFez\Ron and Fez_ Oct 23 2003.mp3','Ron and Fez_ Oct 23 2003.mp3',4000,$now,0,$now,14400000,'FULL-CAPTURE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='P1'),'D:\Radio\RonFez\2003\RaF1023-Part1.mp3','RaF1023-Part1.mp3',2000,$now,0,$now,7200000,'SEGMENT-1','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='P2'),'D:\Radio\RonFez\2003\RaF1023-Part2of2.mp3','RaF1023-Part2of2.mp3',2100,$now,0,$now,7200000,'SEGMENT-2','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var recordings = engine.GetRecordings();
        Equal(2, recordings.Count);
        True(recordings.Any(x => x.Role == "Complete multipart recording" && x.SegmentCount == 2));
        True(recordings.Any(x => x.Role == "Complete alternate recording" && x.SegmentCount == 1));
        Equal(1, recordings.Count(x => x.IsPreferredCandidate));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthClassifiesTruncatedRecording()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-truncated.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2002-03-28','High','Complete','Unplayed',$now,$now,'COMPLETE');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2002-03-28','High','Tiny','Unplayed',$now,$now,'TINY');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='COMPLETE'),'D:\Radio\RonFez\Ron and Fez_ 2002-03-28-V1.mp3','Ron and Fez_ 2002-03-28-V1.mp3',4000,$now,0,$now,14400000,'LONG','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='TINY'),'D:\Radio\RonFez\Ron and Fez_ 2002-03-28-V2.mp3','Ron and Fez_ 2002-03-28-V2.mp3',8,$now,0,$now,8900,'TINY','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var recordings = engine.GetRecordings();
        var truncated = recordings.Single(x => x.Role == "Likely truncated or damaged");
        True(!truncated.IsPreferredCandidate);
        True(recordings.Single(x => x.IsPreferredCandidate).DurationMs > truncated.DurationMs);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthComparesMultipartCoverage()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-multipart-coverage.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-24','High','Full','Unplayed',$now,$now,'FULL-COVERAGE');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-24','High','Part 1','Unplayed',$now,$now,'COVERAGE-P1');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-24','High','Part 2','Unplayed',$now,$now,'COVERAGE-P2');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='FULL-COVERAGE'),'D:\Radio\RonFez\Ron and Fez_ 2003-10-24 full.mp3','Ron and Fez_ 2003-10-24 full.mp3',4000,$now,0,$now,14400000,'COVERAGE-FULL','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='COVERAGE-P1'),'D:\Radio\RonFez\Ron and Fez_ 2003-10-24 Part1of2.mp3','Ron and Fez_ 2003-10-24 Part1of2.mp3',1000,$now,0,$now,1800000,'COVERAGE-1','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='COVERAGE-P2'),'D:\Radio\RonFez\Ron and Fez_ 2003-10-24 Part2of2.mp3','Ron and Fez_ 2003-10-24 Part2of2.mp3',1000,$now,0,$now,1800000,'COVERAGE-2','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var recordings = engine.GetRecordings();
        var multipart = recordings.Single(x => x.SegmentCount == 2);
        Equal("Partial multipart recording", multipart.Role);
        True(multipart.DurationRatio < 0.30);
        True(!multipart.IsPreferredCandidate);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthFlagsSuspiciousMerge()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-suspicious-merge.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-23','High','Long','Unplayed',$now,$now,'MERGE-LONG');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2001-08-23','High','Short','Unplayed',$now,$now,'MERGE-SHORT');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='MERGE-LONG'),'D:\Radio\RonFez\Ron and Fez_ 2001-08-23 source long.mp3','Ron and Fez_ 2001-08-23 source long.mp3',5000,$now,0,$now,18000000,'MERGE-LONG-HASH','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='MERGE-SHORT'),'D:\Radio\RonFez\Ron and Fez_ 2001-08-23 source short.mp3','Ron and Fez_ 2001-08-23 source short.mp3',2000,$now,0,$now,5400000,'MERGE-SHORT-HASH','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var broadcast = engine.GetBroadcasts("suspicious-merges").Single();
        True(broadcast.SuspiciousMerge);
        Equal("Review recommended", broadcast.AdoptionState);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthPropagatesStrongCrossDateConflict()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-strong-conflict.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\RonFez',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2012-11-22','High','Claim A','Unplayed',$now,$now,'CLAIM-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2012-11-23','High','Claim B','Unplayed',$now,$now,'CLAIM-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-A'),'D:\Radio\RonFez\Ron and Fez_ 11_22_2012.mp3','Ron and Fez_ 11_22_2012.mp3',204081569,$now,0,$now,12749783,'SAME-PARTIAL','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='CLAIM-B'),'D:\Radio\RonFez\Ron and Fez_ 11_23_2012.mp3','Ron and Fez_ 11_23_2012.mp3',204081569,$now,0,$now,12749783,'SAME-PARTIAL','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        Equal(1, engine.GetConflicts().Count);
        True(engine.GetConflicts()[0].ConflictType.Contains("Strong audio", StringComparison.OrdinalIgnoreCase));
        Equal(2, engine.GetBroadcasts("blocked").Count);
        True(engine.GetFiles("needs-attention").All(x => x.Warnings.Contains("conflicting", StringComparison.OrdinalIgnoreCase)));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthProducesAdoptionAudit()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-adoption.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Bennington','Bennington');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Bennington',(SELECT id FROM collections WHERE name='Bennington'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Bennington'),'2016-05-17','High','Show','Unplayed',$now,$now,'READY');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='READY'),'D:\Radio\Bennington\Bennington 2016-05-17.mp3','Bennington 2016-05-17.mp3',1000,$now,0,$now,10800000,'READY-PARTIAL','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        Equal(1, engine.GetAdoptionSummary().AdoptionReadyTotal);
        Equal(1, engine.GetYears().Single(x => x.Year == "2016").ProposedBroadcasts);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}



static void LibraryTruthAssemblesVariantFamiliesSafely()
{
    var hour = (long)TimeSpan.FromHours(1).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("Ron and Fez_ 2002-06-25-V1-P1.mp3", "2002-06-25", "", 4 * hour, "V1-P1"),
        ("Ron and Fez_ 2002-06-25-V1-P2.mp3", "2002-06-25", "", 1 * hour, "V1-P2"),
        ("Ron and Fez_ 2002-06-25-V2-P1.mp3", "2002-06-25", "", 2 * hour, "V2-P1"),
        ("Ron and Fez_ 2002-06-25-V2-P2.mp3", "2002-06-25", "", 3 * hour, "V2-P2")
    }, engine =>
    {
        var recordings = engine.GetRecordings();
        Equal(2, recordings.Count);
        True(recordings.All(x => x.SegmentCount == 2));
        True(recordings.All(x => x.DurationMs == 5 * hour));
        Equal("Ready with recording choice", engine.GetBroadcasts().Single().AdoptionState);
    });
}

static void LibraryTruthPromotesBareMultipartSequence()
{
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("20011122 R&F 1.mp3", "2001-11-22", "", (long)TimeSpan.FromHours(3.9).TotalMilliseconds, "BARE-1"),
        ("20011122 R&F 2.mp3", "2001-11-22", "", (long)TimeSpan.FromHours(3.8).TotalMilliseconds, "BARE-2")
    }, engine =>
    {
        var recording = engine.GetRecordings().Single();
        Equal(2, recording.SegmentCount);
        Equal("Complete multipart recording", recording.Role);
        True(recording.Evidence.Contains("contiguous 1..N sequence", StringComparison.OrdinalIgnoreCase));

        var files = engine.GetFiles().OrderBy(x => x.Filename, StringComparer.OrdinalIgnoreCase).ToArray();
        Equal("Part 1 of 2", files[0].ProposedPart);
        Equal("Part 2 of 2", files[1].ProposedPart);
        True(files[1].Evidence.Contains("filename-family", StringComparison.OrdinalIgnoreCase));
    });
}

static void LibraryTruthFlagsProgrammeSpecificDurationClusters()
{
    var longDuration = (long)TimeSpan.FromHours(4.237).TotalMilliseconds;
    var shortDuration = (long)TimeSpan.FromHours(3.093).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-08-23-2002.mp3", "2002-08-23", "", longDuration, "LONG-A"),
        ("Ron and Fez_ Aug 23 2002.mp3", "2002-08-23", "", longDuration, "LONG-B"),
        ("FNR-08-23-2002.mp3", "2002-08-23", "", shortDuration, "FNR-A"),
        ("Ron and Fez_ Aug 23 2002 Eddie Trunk.mp3", "2002-08-23", "", shortDuration, "FNR-B"),
        ("Ron and Fez_ Aug 23 2002 FNR Show.mp3", "2002-08-23", "", shortDuration, "FNR-C")
    }, engine =>
    {
        var broadcast = engine.GetBroadcasts().Single();
        True(broadcast.SuspiciousMerge);
        Equal("Review recommended", broadcast.AdoptionState);
        True(broadcast.AdoptionReason.Contains("duration families", StringComparison.OrdinalIgnoreCase));
    });
}

static void LibraryTruthDetectsCompositeCoverage()
{
    var am = (long)TimeSpan.FromHours(3.886).TotalMilliseconds;
    var pm = (long)TimeSpan.FromHours(3.877).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("20011122 R&F 1.mp3", "2001-11-22", "", am, "COMPOSITE-1"),
        ("20011122 R&F 2.mp3", "2001-11-22", "", pm, "COMPOSITE-2"),
        ("Ron and Fez_ Nov 22 2001 AM.mp3", "2001-11-22", "Morning show", am, "AM"),
        ("Ron and Fez_ Nov 22 2001 PM.mp3", "2001-11-22", "Evening show", pm, "PM")
    }, engine =>
    {
        var standard = engine.GetBroadcasts().Single(x => x.BroadcastSlot == "Standard");
        Equal("Review recommended", standard.AdoptionState);
        True(standard.AdoptionReason.Contains("combined", StringComparison.OrdinalIgnoreCase));
        True(engine.GetBroadcasts().Where(x => x.BroadcastSlot != "Standard").All(x => x.AdoptionState == "Ready"));
        var inferred = engine.GetCoverages(reviewOnly: true).OrderBy(x => x.SegmentNumber).ToArray();
        Equal(2, inferred.Length);
        True(inferred.All(x => x.CoverageKind == "Composite slot coverage"));
        Equal(0L, inferred[0].StartOffsetMs);
        Equal(inferred[0].EndOffsetMs, inferred[1].StartOffsetMs);
        True(inferred.All(x => x.SourceBroadcastKey.Contains("|STANDARD", StringComparison.OrdinalIgnoreCase)));
        True(inferred.Select(x => x.TargetBroadcastKey).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 2);
    });
}

static void LibraryTruthDetectsCrossSlotEquivalent()
{
    var duration = (long)TimeSpan.FromHours(3.971).TotalMilliseconds;
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-10-23-2002.mp3", "2002-10-23", "", duration, "STANDARD"),
        ("Ron and Fez_ Oct 23 2002 AM.mp3", "2002-10-23", "Morning show", duration, "AM")
    }, engine =>
    {
        var standard = engine.GetBroadcasts().Single(x => x.BroadcastSlot == "Standard");
        Equal("Review recommended", standard.AdoptionState);
        True(standard.AdoptionReason.Contains("alternate encode", StringComparison.OrdinalIgnoreCase),
            $"Standard adoption reason was: {standard.AdoptionReason}; coverages: " +
            string.Join(" | ", engine.GetCoverages().Select(x => $"{x.CoverageKind}:{x.SourceBroadcastKey}->{x.TargetBroadcastKey}")));
        var inferred = engine.GetCoverages(reviewOnly: true).Single();
        Equal("Same-date equivalent", inferred.CoverageKind);
        True(inferred.TargetBroadcastKey.Contains("|AM", StringComparison.OrdinalIgnoreCase),
            $"Equivalent target was {inferred.TargetBroadcastKey}");
    });
}

static void LibraryTruthPersistsDirectSegmentCoverage()
{
    RunLibraryTruthScenario("Ron & Fez", new[]
    {
        ("R&F-10-31-2002a.mp3", "2002-10-31", "", (long)TimeSpan.FromHours(2).TotalMilliseconds, "COVERAGE-A"),
        ("R&F-10-31-2002b.mp3", "2002-10-31", "", (long)TimeSpan.FromHours(1.8).TotalMilliseconds, "COVERAGE-B")
    }, engine =>
    {
        var direct = engine.GetCoverages().OrderBy(x => x.SegmentNumber).ToArray();
        Equal(2, direct.Length);
        Equal(1, direct[0].SegmentNumber);
        Equal(2, direct[1].SegmentNumber);
        Equal((int?)2, direct[0].SegmentTotal);
        Equal(direct[0].EndOffsetMs, direct[1].StartOffsetMs);
        True(direct.All(x => x.SourceBroadcastKey == x.TargetBroadcastKey));
        True(direct.All(x => !x.RequiresReview));
    });
}

static void LibraryTruthPreparesGuardedAdoptionPreview()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-alpha6-adoption.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron and Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha6',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','First','Unplayed',$now,$now,'ALPHA6-A');
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_uid)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Second','Unplayed',$now,$now,'ALPHA6-B');
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA6-A'),'D:\Radio\Alpha6\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA6-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA6-B'),'D:\Radio\Alpha6\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA6-TWO','AvailableOffline',1);
                """;
            command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var preview = engine.GetAdoptionPreviews().Single();
        True(preview.EligibleForGuardedAdoption,
            $"Adoption held: state={preview.AdoptionState}; reason={preview.GuardReason}; action={preview.PlannedAction}");
        Equal(2, preview.CurrentEpisodeCount);
        Equal(1, preview.RetireEpisodeCount);
        Equal(1, preview.ReassignFileCount);
        True(preview.ProvisionalEpisodeId.HasValue, $"No provisional survivor: {preview.GuardReason}");
        True(preview.GuardReason.Contains("rollback-verified", StringComparison.OrdinalIgnoreCase),
            $"Unexpected adoption guard: {preview.GuardReason}");
        var summary = engine.GetAdoptionPlanSummary();
        Equal(1, summary.EligibleBroadcasts);
        Equal(1, summary.LiveEpisodeRowsToConsolidate);

        using var verify = database.OpenConnection();
        using var count = verify.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM episodes";
        Equal(2L, Convert.ToInt64(count.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthRehearsalRollsBackDisposableClone()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha7-rehearsal.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        long firstEpisode;
        long secondEpisode;
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha7',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','First capture','In Progress',$now,$now,'Standard','ALPHA7-A',0);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Second capture','Completed',$now,$now,'Standard','ALPHA7-B',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-A'),'D:\Radio\Alpha7\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA7-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-B'),'D:\Radio\Alpha7\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA7-TWO','AvailableOffline',1);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-A'),5000,0,$now,1,10800000,1.0,NULL,$now,0);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-B'),10810000,1,$now,2,10810000,1.25,$now,$now,1);
                INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA7-B'),60000,'Moment','Preserve me',$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();

            using var ids = connection.CreateCommand();
            ids.CommandText = "SELECT id FROM episodes ORDER BY id";
            using var reader = ids.ExecuteReader();
            reader.Read();
            firstEpisode = reader.GetInt64(0);
            reader.Read();
            secondEpisode = reader.GetInt64(0);
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var rehearsal = new LibraryTruthAdoptionRehearsalService(database);
        var summary = rehearsal.Run(backupDirectory);

        True(summary.RollbackVerified);
        Equal("ok", summary.IntegrityCheck);
        Equal("ok", summary.BackupRestoreCheck);
        Equal(1, summary.EligibleBroadcasts);
        Equal(1, summary.FileReassignments);
        Equal(1, summary.AliasRowsRetired);
        True(File.Exists(summary.BackupPath));
        Equal(summary.SourceFingerprint, summary.RollbackFingerprint);
        Equal(1, rehearsal.GetLatestItems().Count);

        using var verify = database.OpenConnection();
        using var episodes = verify.CreateCommand();
        episodes.CommandText = "SELECT COUNT(*),SUM(hidden),SUM(favourite) FROM episodes";
        using var episodeReader = episodes.ExecuteReader();
        episodeReader.Read();
        Equal(2L, episodeReader.GetInt64(0));
        Equal(0L, episodeReader.GetInt64(1));
        Equal(1L, episodeReader.GetInt64(2));

        using var media = verify.CreateCommand();
        media.CommandText = "SELECT episode_id FROM media_files ORDER BY id";
        using var mediaReader = media.ExecuteReader();
        mediaReader.Read();
        Equal(firstEpisode, mediaReader.GetInt64(0));
        mediaReader.Read();
        Equal(secondEpisode, mediaReader.GetInt64(0));

        using var state = verify.CreateCommand();
        state.CommandText = "SELECT COUNT(*) FROM playback_state";
        Equal(2L, Convert.ToInt64(state.ExecuteScalar()));
        using var moments = verify.CreateCommand();
        moments.CommandText = "SELECT episode_id FROM moments";
        Equal(secondEpisode, Convert.ToInt64(moments.ExecuteScalar()));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void LibraryTruthGuardedAdoptionCommitsVerifiedPlan()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha10-adoption.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha10',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','First capture','In Progress',$now,$now,'Standard','ALPHA10-A',0);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,favourite)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Second capture','Completed',$now,$now,'Standard','ALPHA10-B',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-A'),'D:\Radio\Alpha10\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA10-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-B'),'D:\Radio\Alpha10\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA10-TWO','AvailableOffline',1);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-A'),5000,0,$now,1,10800000,1.0,NULL,$now,0);
                INSERT INTO playback_state(episode_id,position_ms,completed,last_played_at,play_count,duration_ms,playback_speed,completed_at,first_played_at,completion_count)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-B'),10810000,1,$now,2,10810000,1.25,$now,$now,1);
                INSERT INTO moments(episode_id,position_ms,title,notes,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA10-B'),60000,'Moment','Preserve me',$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var adoption = new LibraryTruthAdoptionRehearsalService(database);
        var rehearsal = adoption.Run(backupDirectory);
        True(rehearsal.RollbackVerified);
        Equal(64, rehearsal.TruthRunSignature.Length);
        Equal(64, rehearsal.ItemSignature.Length);
        Equal(64, rehearsal.ConflictSignature.Length);

        var eligibility = adoption.GetAdoptionEligibility();
        True(eligibility.CanAdopt);
        Equal(rehearsal.SourceFingerprint, eligibility.CurrentSourceFingerprint);
        Equal(rehearsal.TruthRunSignature, eligibility.ExpectedTruthRunSignature);
        Equal(rehearsal.ItemSignature, eligibility.ExpectedItemSignature);
        Equal(rehearsal.ConflictSignature, eligibility.ExpectedConflictSignature);

        string originalGuardReason;
        using (var shadowTamper = database.OpenConnection())
        {
            using var read = shadowTamper.CreateCommand();
            read.CommandText = "SELECT guard_reason FROM library_truth_adoption_previews WHERE run_id=$run ORDER BY id LIMIT 1";
            read.Parameters.AddWithValue("$run", rehearsal.TruthRunId);
            originalGuardReason = Convert.ToString(read.ExecuteScalar()) ?? string.Empty;
            using var command = shadowTamper.CreateCommand();
            command.CommandText = "UPDATE library_truth_adoption_previews SET guard_reason=guard_reason || ' tampered' WHERE id=(SELECT MIN(id) FROM library_truth_adoption_previews WHERE run_id=$run)";
            command.Parameters.AddWithValue("$run", rehearsal.TruthRunId);
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var shadowRestore = database.OpenConnection())
        {
            using var command = shadowRestore.CreateCommand();
            command.CommandText = "UPDATE library_truth_adoption_previews SET guard_reason=$value WHERE id=(SELECT MIN(id) FROM library_truth_adoption_previews WHERE run_id=$run)";
            command.Parameters.AddWithValue("$value", originalGuardReason);
            command.Parameters.AddWithValue("$run", rehearsal.TruthRunId);
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        string originalOutcome;
        using (var ledgerTamper = database.OpenConnection())
        {
            using var read = ledgerTamper.CreateCommand();
            read.CommandText = "SELECT outcome FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run ORDER BY id LIMIT 1";
            read.Parameters.AddWithValue("$run", rehearsal.Id);
            originalOutcome = Convert.ToString(read.ExecuteScalar()) ?? string.Empty;
            using var command = ledgerTamper.CreateCommand();
            command.CommandText = "UPDATE library_truth_rehearsal_items SET outcome='Tampered' WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run)";
            command.Parameters.AddWithValue("$run", rehearsal.Id);
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var ledgerRestore = database.OpenConnection())
        {
            using var command = ledgerRestore.CreateCommand();
            command.CommandText = "UPDATE library_truth_rehearsal_items SET outcome=$value WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_items WHERE rehearsal_run_id=$run)";
            command.Parameters.AddWithValue("$value", originalOutcome);
            command.Parameters.AddWithValue("$run", rehearsal.Id);
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        if (rehearsal.MetadataConflicts > 0)
        {
            string originalResolution;
            using (var conflictTamper = database.OpenConnection())
            {
                using var read = conflictTamper.CreateCommand();
                read.CommandText = "SELECT resolution FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run ORDER BY id LIMIT 1";
                read.Parameters.AddWithValue("$run", rehearsal.Id);
                originalResolution = Convert.ToString(read.ExecuteScalar()) ?? string.Empty;
                using var command = conflictTamper.CreateCommand();
                command.CommandText = "UPDATE library_truth_rehearsal_conflicts SET resolution='tampered' WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run)";
                command.Parameters.AddWithValue("$run", rehearsal.Id);
                command.ExecuteNonQuery();
            }
            True(!adoption.GetAdoptionEligibility().CanAdopt);
            using (var conflictRestore = database.OpenConnection())
            {
                using var command = conflictRestore.CreateCommand();
                command.CommandText = "UPDATE library_truth_rehearsal_conflicts SET resolution=$value WHERE id=(SELECT MIN(id) FROM library_truth_rehearsal_conflicts WHERE rehearsal_run_id=$run)";
                command.Parameters.AddWithValue("$value", originalResolution);
                command.Parameters.AddWithValue("$run", rehearsal.Id);
                command.ExecuteNonQuery();
            }
            True(adoption.GetAdoptionEligibility().CanAdopt);
        }

        using (var interrupted = database.OpenConnection())
        {
            using var command = interrupted.CreateCommand();
            command.CommandText = """
                INSERT INTO library_truth_adoption_runs(
                    truth_run_id,rehearsal_run_id,app_version,started_at,status,backup_path,message)
                VALUES($truth,$rehearsal,'test-interrupted',$started,'running','D:\Backups\interrupted.db','Simulated interrupted attempt')
                """;
            command.Parameters.AddWithValue("$truth", rehearsal.TruthRunId);
            command.Parameters.AddWithValue("$rehearsal", rehearsal.Id);
            command.Parameters.AddWithValue("$started", DateTimeOffset.UtcNow.ToString("O"));
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var removeInterrupted = database.OpenConnection())
        {
            using var command = removeInterrupted.CreateCommand();
            command.CommandText = "DELETE FROM library_truth_adoption_runs WHERE app_version='test-interrupted'";
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        using (var changed = database.OpenConnection())
        {
            using var command = changed.CreateCommand();
            command.CommandText = "UPDATE episodes SET title='Changed after rehearsal' WHERE broadcast_uid='ALPHA10-A'";
            command.ExecuteNonQuery();
        }
        True(!adoption.GetAdoptionEligibility().CanAdopt);
        using (var restored = database.OpenConnection())
        {
            using var command = restored.CreateCommand();
            command.CommandText = "UPDATE episodes SET title='First capture' WHERE broadcast_uid='ALPHA10-A'";
            command.ExecuteNonQuery();
        }
        True(adoption.GetAdoptionEligibility().CanAdopt);

        var committed = adoption.AdoptVerifiedPlan(backupDirectory, "test-alpha10");
        Equal("completed", committed.Status);
        True(committed.CommitVerified);
        Equal("ok", committed.IntegrityCheck);
        Equal("ok", committed.BackupRestoreCheck);
        Equal(committed.StagedFingerprint, committed.PostCommitFingerprint);
        Equal(committed.RehearsalTruthSignature, committed.CommitTruthSignature);
        Equal(rehearsal.TruthRunSignature, committed.CommitTruthSignature);
        Equal(64, committed.CommitTruthSignature.Length);
        True(File.Exists(committed.BackupPath));

        using var verify = database.OpenConnection();
        using var structure = verify.CreateCommand();
        structure.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM canonical_broadcasts),
                (SELECT COUNT(*) FROM recordings),
                (SELECT COUNT(*) FROM recording_segments),
                (SELECT COUNT(*) FROM recording_coverages),
                (SELECT COUNT(*) FROM episode_canonical_map),
                (SELECT COUNT(*) FROM library_truth_adoption_items),
                (SELECT COUNT(*) FROM library_truth_adoption_conflicts)
            """;
        using var structureReader = structure.ExecuteReader();
        structureReader.Read();
        Equal((long)committed.CanonicalWrites, structureReader.GetInt64(0));
        Equal((long)committed.RecordingWrites, structureReader.GetInt64(1));
        Equal((long)committed.SegmentWrites, structureReader.GetInt64(2));
        Equal((long)committed.CoverageWrites, structureReader.GetInt64(3));
        Equal(2L, structureReader.GetInt64(4));
        Equal((long)committed.EligibleBroadcasts, structureReader.GetInt64(5));
        Equal((long)committed.MetadataConflicts, structureReader.GetInt64(6));
        structureReader.Close();

        using var survivor = verify.CreateCommand();
        survivor.CommandText = "SELECT survivor_episode_id FROM episode_canonical_map WHERE is_survivor=1";
        var survivorId = Convert.ToInt64(survivor.ExecuteScalar());
        using var live = verify.CreateCommand();
        live.CommandText = """
            SELECT
                (SELECT COUNT(*) FROM media_files WHERE episode_id=$survivor),
                (SELECT COUNT(*) FROM episodes WHERE hidden=1),
                (SELECT COUNT(*) FROM playback_state),
                (SELECT COUNT(*) FROM moments WHERE episode_id=$survivor)
            """;
        live.Parameters.AddWithValue("$survivor", survivorId);
        using var liveReader = live.ExecuteReader();
        liveReader.Read();
        Equal(2L, liveReader.GetInt64(0));
        Equal(1L, liveReader.GetInt64(1));
        Equal(1L, liveReader.GetInt64(2));
        Equal(1L, liveReader.GetInt64(3));
        liveReader.Close();

        True(!adoption.GetAdoptionEligibility().CanAdopt);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void LibraryTruthClassifiesMetadataConflicts()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha8-forensics.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha8',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,notes,hosts,metadata_confidence)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Ron & Fez archive broadcast','Unplayed',$now,$now,'Standard','ALPHA8-A','','Ron Bennington',30);
                INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,notes,hosts,metadata_confidence)
                VALUES((SELECT id FROM collections WHERE name='Ron & Fez'),'2005-05-12','High','Billy Staples visits Ron & Fez','Unplayed',$now,$now,'Standard','ALPHA8-B','Detailed researched note','Fez Whatley',85);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-A'),'D:\Radio\Alpha8\a.mp3','R&F 05-12-2005.mp3',1000,$now,0,$now,10800000,'ALPHA8-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-B'),'D:\Radio\Alpha8\b.mp3','Ron and Fez_ 2005-05-12.mp3',1200,$now,0,$now,10810000,'ALPHA8-TWO','AvailableOffline',1);
                INSERT INTO research_field_provenance(episode_id,field_name,value_text,source_kind,source_label,confidence,evidence_count,protected,active,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-B'),'headline','Billy Staples visits Ron & Fez','research_pack','Verified pack',95,3,1,1,$now);
                INSERT INTO research_field_provenance(episode_id,field_name,value_text,source_kind,source_label,confidence,evidence_count,protected,active,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA8-A'),'headline','An unrelated old protected value','manual','Old unmatched edit',100,5,1,1,$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var rehearsal = new LibraryTruthAdoptionRehearsalService(database);
        var summary = rehearsal.Run(backupDirectory);
        var conflicts = rehearsal.GetLatestConflictForensics();

        True(summary.RollbackVerified);
        True(summary.AutoResolvedConflicts >= 3);
        Equal(0, summary.UnresolvedConflicts);
        True(summary.PreservedAlternates >= 2);
        True(conflicts.Any(x => x.FieldName == "title" && x.AutoResolved && x.Classification == "Specific over placeholder"));
        True(conflicts.Where(x => x.FieldName == "title").All(x => !x.Provenance.Contains("Old unmatched edit", StringComparison.Ordinal)));
        True(conflicts.Any(x => x.FieldName == "notes" && x.AutoResolved && x.Classification == "Empty vs populated"));
        True(conflicts.Any(x => x.FieldName == "hosts" && x.AutoResolved && x.Classification == "Mergeable union"));
        True(conflicts.All(x => !string.IsNullOrWhiteSpace(x.CandidateValues)));

        using var verify = database.OpenConnection();
        using var titles = verify.CreateCommand();
        titles.CommandText = "SELECT GROUP_CONCAT(title,'|') FROM episodes ORDER BY id";
        var liveTitles = Convert.ToString(titles.ExecuteScalar()) ?? string.Empty;
        True(liveTitles.Contains("Ron & Fez archive broadcast", StringComparison.Ordinal));
        True(liveTitles.Contains("Billy Staples visits Ron & Fez", StringComparison.Ordinal));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}


static void LibraryTruthRefinesGeneratedMetadataPolicies()
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    var backupDirectory = Path.Combine(directory, "Backups");
    Directory.CreateDirectory(backupDirectory);
    var databasePath = Path.Combine(directory, "truth-alpha9-policies.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = """
                INSERT OR IGNORE INTO collections(name,sort_name) VALUES('Ron & Fez','Ron & Fez');
                INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled)
                VALUES('D:\Radio\Alpha9',(SELECT id FROM collections WHERE name='Ron & Fez'),1,1);
                INSERT INTO episodes(
                    collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,
                    artwork_path,edition,broadcast_variant,broadcast_era,metadata_confidence,user_modified)
                VALUES(
                    (SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-03','High','FriOct032003-pt1','Unplayed',$now,$now,
                    'Evening show','ALPHA9-A','D:\Artwork\old.jpg','Commercial-free','Archive part 2','WJFK Washington era',40,1);
                INSERT INTO episodes(
                    collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,broadcast_uid,
                    artwork_path,edition,broadcast_variant,broadcast_era,metadata_confidence,user_modified)
                VALUES(
                    (SELECT id FROM collections WHERE name='Ron & Fez'),'2003-10-03','High','WJFK evening show — Friday, 3 October 2003','Unplayed',$now,$now,
                    'Evening show','ALPHA9-B','D:\Artwork\survivor.jpg','WJFK-FM (106.7, Washington, D.C.)','Primary archive recording','WJFK Washington/Fairfax era',85,0);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA9-A'),'D:\Radio\Alpha9\a.mp3','Ron and Fez_ 2003-10-03-Part1.mp3',1000,$now,0,$now,5400000,'ALPHA9-ONE','AvailableOffline',1);
                INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA9-B'),'D:\Radio\Alpha9\b.mp3','Ron and Fez_ 2003-10-03.mp3',1200,$now,0,$now,10800000,'ALPHA9-TWO','AvailableOffline',1);
                INSERT INTO research_field_provenance(episode_id,field_name,value_text,source_kind,source_label,confidence,evidence_count,protected,active,created_at)
                VALUES((SELECT id FROM episodes WHERE broadcast_uid='ALPHA9-B'),'station','WJFK-FM (106.7, Washington, D.C.)','research_pack','Verified station evidence',95,3,0,1,$now);
                """;
            setup.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
            setup.ExecuteNonQuery();
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        var rehearsal = new LibraryTruthAdoptionRehearsalService(database);
        var summary = rehearsal.Run(backupDirectory);
        var conflicts = rehearsal.GetLatestConflictForensics();

        True(summary.RollbackVerified);
        Equal(0, summary.UnresolvedConflicts);
        if (conflicts.Count == 0)
            throw new InvalidOperationException("No Alpha9 conflict policies were generated. Broadcasts: " +
                string.Join(" | ", engine.GetBroadcasts().Select(x => $"{x.CanonicalKey}:{x.AdoptionState}:{x.AdoptionReason}")) +
                "; previews: " + string.Join(" | ", engine.GetAdoptionPreviews().Select(x => $"{x.CanonicalKey}:{x.GuardReason}")));
        True(conflicts.Any(x => x.FieldName == "title" && x.AutoResolved &&
                                x.Classification == "Descriptive title over filename" &&
                                x.SelectedValue.Contains("WJFK evening show", StringComparison.Ordinal)),
            "Conflict policies: " + string.Join(" | ", conflicts.Select(x => $"{x.FieldName}:{x.Classification}:{x.SelectedValue}:{x.AutoResolved}")));
        True(conflicts.Any(x => x.FieldName == "broadcast_variant" && x.AutoResolved &&
                                x.Classification == "Recording-level variant" && x.SelectedValue == string.Empty));
        True(conflicts.Any(x => x.FieldName == "broadcast_era" && x.AutoResolved &&
                                x.Classification == "Generated era winner"));
        True(conflicts.Any(x => x.FieldName == "artwork_path" && x.AutoResolved &&
                                x.Classification == "Survivor artwork"));
        True(conflicts.Any(x => x.FieldName == "edition" && x.AutoResolved &&
                                x.SelectedValue.Contains("WJFK-FM", StringComparison.Ordinal)));
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void RunLibraryTruthScenario(
    string collection,
    IReadOnlyList<(string Filename, string Date, string Slot, long DurationMs, string PartialHash)> files,
    Action<LibraryTruthEngine> assertion)
{
    var directory = Path.Combine(Path.GetTempPath(), "RadioVaultTests", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var databasePath = Path.Combine(directory, "truth-alpha6.sqlite");
    try
    {
        var database = new SqliteDatabase(databasePath);
        database.Initialize();
        using (var connection = database.OpenConnection())
        {
            using var setup = connection.CreateCommand();
            setup.CommandText = "INSERT OR IGNORE INTO collections(name,sort_name) VALUES($collection,$collection);" +
                                "INSERT INTO library_folders(path,assigned_collection_id,recursive,enabled) " +
                                "VALUES($root,(SELECT id FROM collections WHERE name=$collection),1,1);";
            setup.Parameters.AddWithValue("$collection", collection);
            setup.Parameters.AddWithValue("$root", @"D:\Radio\Alpha6");
            setup.ExecuteNonQuery();

            for (var index = 0; index < files.Count; index++)
            {
                var file = files[index];
                using var insert = connection.CreateCommand();
                insert.CommandText = """
                    INSERT INTO episodes(collection_id,air_date,date_confidence,title,status,date_added,updated_at,broadcast_slot,part_number,total_parts,broadcast_uid)
                    VALUES((SELECT id FROM collections WHERE name=$collection),$date,'High',$title,'Unplayed',$now,$now,$slot,1,NULL,$uid);
                    INSERT INTO media_files(episode_id,path,original_filename,file_size,modified_time,is_missing,last_seen_at,duration_ms,partial_hash,storage_state,is_preferred)
                    VALUES((SELECT id FROM episodes WHERE broadcast_uid=$uid),$path,$filename,$size,$now,0,$now,$duration,$hash,'AvailableOffline',1);
                    """;
                insert.Parameters.AddWithValue("$collection", collection);
                insert.Parameters.AddWithValue("$date", file.Date);
                insert.Parameters.AddWithValue("$title", file.Filename);
                insert.Parameters.AddWithValue("$slot", file.Slot);
                insert.Parameters.AddWithValue("$uid", $"ALPHA6-{index}-{Guid.NewGuid():N}");
                insert.Parameters.AddWithValue("$path", Path.Combine(@"D:\Radio\Alpha6", file.Filename));
                insert.Parameters.AddWithValue("$filename", file.Filename);
                insert.Parameters.AddWithValue("$size", Math.Max(1000, file.DurationMs / 10));
                insert.Parameters.AddWithValue("$duration", file.DurationMs);
                insert.Parameters.AddWithValue("$hash", file.PartialHash);
                insert.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
                insert.ExecuteNonQuery();
            }
        }

        var engine = new LibraryTruthEngine(database);
        engine.BuildShadowIndex();
        assertion(engine);
    }
    finally
    {
        try { Directory.Delete(directory, true); } catch { }
    }
}

static void Equal<T>(T expected, T actual)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"Expected {expected}, got {actual}.");
}

static void True(bool value, string? message = null)
{
    if (!value) throw new InvalidOperationException(message ?? "Expected true.");
}
