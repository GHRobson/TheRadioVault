function ReadNextVersionNumber(const Value: String; var Position: Integer; var Found: Boolean): Integer;
var
  Start: Integer;
begin
  while (Position <= Length(Value)) and ((Value[Position] < '0') or (Value[Position] > '9')) do
    Position := Position + 1;
  Found := Position <= Length(Value);
  if not Found then
  begin
    Result := 0;
    exit;
  end;

  Start := Position;
  while (Position <= Length(Value)) and (Value[Position] >= '0') and (Value[Position] <= '9') do
    Position := Position + 1;
  Result := StrToIntDef(Copy(Value, Start, Position - Start), 0);
end;

function CompareVersionNumbers(const Left, Right: String): Integer;
var
  LeftPosition, RightPosition, LeftNumber, RightNumber: Integer;
  LeftFound, RightFound: Boolean;
begin
  LeftPosition := 1;
  RightPosition := 1;
  repeat
    LeftNumber := ReadNextVersionNumber(Left, LeftPosition, LeftFound);
    RightNumber := ReadNextVersionNumber(Right, RightPosition, RightFound);
    if LeftNumber > RightNumber then
    begin
      Result := 1;
      exit;
    end;
    if LeftNumber < RightNumber then
    begin
      Result := -1;
      exit;
    end;
  until (not LeftFound) and (not RightFound);
  Result := 0;
end;

function PrereleaseRank(const Suffix: String): Integer;
var
  Normalized: String;
begin
  Normalized := Lowercase(Suffix);
  if Pos('rc', Normalized) = 1 then Result := 30
  else if Pos('beta', Normalized) = 1 then Result := 20
  else if Pos('alpha', Normalized) = 1 then Result := 10
  else Result := 1;
end;

function CompareRadioVaultVersions(const Left, Right: String): Integer;
var
  LeftDash, RightDash, BaseComparison, LeftRank, RightRank: Integer;
  LeftBase, RightBase, LeftSuffix, RightSuffix: String;
begin
  LeftDash := Pos('-', Left);
  RightDash := Pos('-', Right);
  if LeftDash = 0 then
  begin
    LeftBase := Left;
    LeftSuffix := '';
  end
  else
  begin
    LeftBase := Copy(Left, 1, LeftDash - 1);
    LeftSuffix := Copy(Left, LeftDash + 1, Length(Left));
  end;
  if RightDash = 0 then
  begin
    RightBase := Right;
    RightSuffix := '';
  end
  else
  begin
    RightBase := Copy(Right, 1, RightDash - 1);
    RightSuffix := Copy(Right, RightDash + 1, Length(Right));
  end;

  BaseComparison := CompareVersionNumbers(LeftBase, RightBase);
  if BaseComparison <> 0 then
  begin
    Result := BaseComparison;
    exit;
  end;

  { A stable release is newer than any prerelease with the same base version. }
  if (LeftSuffix = '') and (RightSuffix <> '') then
  begin
    Result := 1;
    exit;
  end;
  if (LeftSuffix <> '') and (RightSuffix = '') then
  begin
    Result := -1;
    exit;
  end;
  if (LeftSuffix = '') and (RightSuffix = '') then
  begin
    Result := 0;
    exit;
  end;

  LeftRank := PrereleaseRank(LeftSuffix);
  RightRank := PrereleaseRank(RightSuffix);
  if LeftRank > RightRank then Result := 1
  else if LeftRank < RightRank then Result := -1
  else Result := CompareVersionNumbers(LeftSuffix, RightSuffix);
end;

function PreventRadioVaultDowngrade(const UninstallKey, CandidateVersion: String): Boolean;
var
  InstalledVersion: String;
begin
  Result := True;
  if RegQueryStringValue(HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + UninstallKey,
       'DisplayVersion', InstalledVersion) and
     (CompareRadioVaultVersions(InstalledVersion, CandidateVersion) > 0) then
  begin
    SuppressibleMsgBox(
      'A newer Radio Vault version (' + InstalledVersion + ') is already installed.' + #13#10 + #13#10 +
      'This older installer (' + CandidateVersion + ') has been stopped so it cannot replace newer features. Uninstall the newer version first only if you intentionally want to downgrade.',
      mbCriticalError, MB_OK, IDOK);
    Result := False;
  end;
end;
