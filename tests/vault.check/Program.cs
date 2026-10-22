using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using neat;

// Self-check of the password vault. Plain asserts, no test package: it prints one line per
// check and exits with 1 if any failed, which fails the CI build.
//
// DPAPI only exists on Windows, so everything else runs against TestWrap, a stand-in that
// seals the key with AES-GCM. The checks on the real DPAPI run only on Windows (the CI runner).

internal static class Program
{
    // Few rounds, so that the many password checks below stay quick. One check at the end
    // runs the real count.
    private const int Rounds = 1000;

    private static int _ran;
    private static int _failed;

    private static int Main()
    {
        Run("a saved login can be listed and revealed", RoundTrip);
        Run("an expired or revoked sign-in cannot reveal", Expired);
        Run("nothing secret is in the file in the clear", NothingInClear);
        Run("the vault is the same after reopening", Reopen);
        Run("the same text is sealed differently each time", FreshNonce);
        Run("a password moved to another entry does not open", SwapBetweenEntries);
        Run("a user name moved into the password slot does not open", SwapFields);
        Run("a changed site breaks the entry", ChangedSite);
        Run("a key that Windows will not open is reported and the file kept", KeyLost);
        Run("an unreadable or too-new file is reported and kept", BadFiles);
        Run("a damaged entry does not let the vault be re-protected", DamagedBlocksSwitch);
        Run("password mode: switch, lock, wrong and right password", PasswordMode);
        Run("switching mode replaces the data key", KeyIsReplaced);
        Run("too-short passwords are refused", ShortPassword);
        Run("wrong passwords are slowed down, then allowed again", Throttle);
        Run("changing the password", ChangePassword);
        Run("back to Windows protection", BackToDpapi);
        Run("a password typed in either Unicode form unlocks", UnicodeForms);
        Run("update and remove", UpdateRemove);
        Run("find uses the exact origin only", FindExact);
        Run("the file is swapped in whole and leaves no temporary file", AtomicWrite);
        Run("origins are written one way", Origins);
        Run("a secret is wiped when disposed", SecretWiped);
        Run("check tests a password without opening or changing anything", CheckPassword);
        Run("the gate says what protects the vault", GateProtection);
        Run("the gate asks Windows Hello, and an error is a no", GateHello);
        Run("the gate opens a password vault with the password", GatePassword);
        Run("an access lasts from the last use, not from the start", GateSliding);
        Run("an unused access ends and a password vault is shut again", GateTickAndSignOut);
        Run("the gate and the vault work together to reveal", GateReveal);
        Run("the vault code never logs", NoLogging);
        Run("the real number of PBKDF2 rounds works", RealRounds);

        if (OperatingSystem.IsWindows())
            Run("DPAPI seals and opens, and refuses garbage", RealDpapi);
        else
            Console.WriteLine("skip  DPAPI (Windows only)");

        Console.WriteLine();
        Console.WriteLine($"{_ran - _failed} of {_ran} checks passed.");
        return _failed == 0 ? 0 : 1;
    }

    // ---- small harness ----

    private static void Run(string name, Action check)
    {
        _ran++;
        try
        {
            check();
            Console.WriteLine("ok    " + name);
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine("FAIL  " + name);
            Console.WriteLine("        " + ex.GetType().Name + ": " + ex.Message);
        }
    }

    private static void Expect(bool condition, string what)
    {
        if (!condition)
            throw new Exception(what);
    }

    private static void ExpectThrows<T>(Action action, string what)
        where T : Exception
    {
        try
        {
            action();
        }
        catch (T)
        {
            return;
        }

        throw new Exception(what);
    }

    private static string TempFile()
    {
        var folder = Path.Combine(Path.GetTempPath(), "neat-vault-check-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return Path.Combine(folder, "vault.json");
    }

    private static Vault NewVault(string path, IKeyWrap? wrap = null)
    {
        return new Vault(path, wrap ?? new TestWrap(Fixed), Rounds);
    }

    private static readonly byte[] Fixed = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();

    private static readonly Access Good = new(TimeSpan.FromMinutes(5));

    private static JsonObject Read(string path)
    {
        return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
    }

    private static void Save(string path, JsonObject json)
    {
        File.WriteAllText(path, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static string RevealText(Vault v, Guid id)
    {
        using var secret = v.Reveal(id, Good) ?? throw new Exception("reveal gave nothing");
        return secret.Text();
    }

    // ---- checks ----

    private static void RoundTrip()
    {
        var v = NewVault(TempFile());
        Expect(v.Open() == OpenResult.New, "a new vault was expected");
        Expect(v.IsOpen && v.Mode == VaultMode.Dpapi, "new vaults are open and in Dpapi mode");

        var id = v.Add("https://Example.com:443/login?next=1", "alice@example.com", "s3cret pass \u00e9\u00e8");
        var list = v.List();

        Expect(list.Count == 1, "one entry");
        Expect(list[0].Id == id && list[0].Site == "https://example.com", "site is kept as its origin");
        Expect(list[0].User == "alice@example.com" && !list[0].Damaged, "user name comes back");
        Expect(RevealText(v, id) == "s3cret pass \u00e9\u00e8", "password comes back, accents included");
    }

    private static void Expired()
    {
        var v = NewVault(TempFile());
        v.Open();
        var id = v.Add("https://a.example", "u", "p");

        var now = DateTime.UtcNow;
        var access = new Access(TimeSpan.FromMinutes(2), () => now);
        Expect(access.Valid, "valid at first");

        now = now.AddMinutes(3);
        Expect(!access.Valid, "expired after its time");
        ExpectThrows<UnauthorizedAccessException>(() => v.Reveal(id, access), "expired access must be refused");

        var revoked = new Access(TimeSpan.FromMinutes(5));
        revoked.Revoke();
        ExpectThrows<UnauthorizedAccessException>(() => v.Reveal(id, revoked), "revoked access must be refused");
    }

    private static void NothingInClear()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        v.Add("https://example.com", "very-private-user", "very-private-password");

        var text = File.ReadAllText(path);
        Expect(!text.Contains("very-private-user"), "the user name is in the file in the clear");
        Expect(!text.Contains("very-private-password"), "the password is in the file in the clear");
        Expect(!text.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("very-private-password"))), "the password is in the file as plain base64");
        Expect(text.Contains("https://example.com"), "the site is kept in the clear on purpose, to look entries up");
    }

    private static void Reopen()
    {
        var path = TempFile();
        var first = NewVault(path);
        first.Open();
        var id = first.Add("https://example.com", "bob", "hunter2 hunter2");

        var second = NewVault(path);
        Expect(second.Open() == OpenResult.Ready, "an existing Dpapi vault opens by itself");
        Expect(second.List().Single().User == "bob", "entry is still there");
        Expect(RevealText(second, id) == "hunter2 hunter2", "password is still there");
    }

    private static void FreshNonce()
    {
        // The tag depends on which entry a value belongs to, so comparing whole blocks would
        // hide a repeated nonce. Compare the nonces themselves, over many identical values.
        var path = TempFile();
        var v = NewVault(path);
        v.Open();

        for (var i = 0; i < 30; i++)
            v.Add("https://example.com", "same", "same");

        var nonces = new HashSet<string>();
        var count = 0;
        foreach (var row in Read(path)["Logins"]!.AsArray())
        {
            foreach (var field in new[] { "User", "Pass" })
            {
                var blob = Convert.FromBase64String(row![field]!.GetValue<string>());
                nonces.Add(Convert.ToHexString(blob.AsSpan(0, Seal.NonceBytes)));
                count++;
            }
        }

        Expect(count == 60, "sixty sealed values were expected");
        Expect(nonces.Count == count, "a nonce was used twice");

        // Sealing the same entry again (an update) must not reuse the nonce either.
        var id = v.List()[0].Id;
        var before = Convert.FromBase64String(Read(path)["Logins"]!.AsArray().First(r => r!["Id"]!.GetValue<string>() == id.ToString("N"))!["Pass"]!.GetValue<string>());
        v.Update(id, "https://example.com", "same", "same");
        var after = Convert.FromBase64String(Read(path)["Logins"]!.AsArray().First(r => r!["Id"]!.GetValue<string>() == id.ToString("N"))!["Pass"]!.GetValue<string>());
        Expect(!before.AsSpan(0, Seal.NonceBytes).SequenceEqual(after.AsSpan(0, Seal.NonceBytes)), "an update reused the nonce");
    }

    private static void SwapBetweenEntries()
    {
        // Two cases: entries of different sites, and entries of the SAME site, where only the
        // entry id tells them apart.
        foreach (var sites in new[] { ("https://one.example", "https://two.example"), ("https://same.example", "https://same.example") })
        {
            var path = TempFile();
            var v = NewVault(path);
            v.Open();
            var a = v.Add(sites.Item1, "a", "password-of-a");
            var b = v.Add(sites.Item2, "b", "password-of-b");

            // Move the sealed password of the first entry into the second.
            var json = Read(path);
            var rows = json["Logins"]!.AsArray();
            var first = rows.Single(r => r!["Id"]!.GetValue<string>() == a.ToString("N"))!;
            var second = rows.Single(r => r!["Id"]!.GetValue<string>() == b.ToString("N"))!;
            second["Pass"] = first["Pass"]!.GetValue<string>();
            Save(path, json);

            var again = NewVault(path);
            again.Open();
            Expect(again.Reveal(b, Good) is null, $"the moved password opened in the other entry ({sites.Item1} / {sites.Item2})");
            Expect(RevealText(again, a) == "password-of-a", "the untouched entry still opens");
        }
    }

    private static void SwapFields()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        var id = v.Add("https://example.com", "the-user", "the-password");

        var json = Read(path);
        var row = json["Logins"]![0]!;
        var user = row["User"]!.GetValue<string>();
        row["User"] = row["Pass"]!.GetValue<string>();
        row["Pass"] = user;
        Save(path, json);

        var again = NewVault(path);
        again.Open();
        Expect(again.List().Single().Damaged, "swapped fields should show as damaged");
        Expect(again.Reveal(id, Good) is null, "swapped fields should not open");
    }

    private static void ChangedSite()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        var id = v.Add("https://bank.example", "u", "p");

        // Someone edits the file so the entry looks like it belongs to another site.
        var json = Read(path);
        json["Logins"]![0]!["Site"] = "https://evil.example";
        Save(path, json);

        var again = NewVault(path);
        again.Open();
        Expect(again.Reveal(id, Good) is null, "an entry moved to another site opened");
        Expect(again.List().Single().Damaged, "it should show as damaged");
    }

    private static void KeyLost()
    {
        var path = TempFile();
        var mine = NewVault(path);
        mine.Open();
        mine.Add("https://example.com", "u", "p");
        var before = File.ReadAllBytes(path);

        // Another Windows user cannot open the key.
        var other = NewVault(path, new TestWrap(RandomNumberGenerator.GetBytes(32)));
        Expect(other.Open() == OpenResult.KeyLost, "a key from another user must not open");
        Expect(!other.IsOpen, "the vault must stay shut");
        ExpectThrows<InvalidOperationException>(() => other.List(), "listing a shut vault must fail");
        ExpectThrows<InvalidOperationException>(() => other.Add("https://x.example", "u", "p"), "adding to a shut vault must fail");
        Expect(other.Unlock() == VaultResult.KeyLost, "unlock says the key is lost");
        Expect(before.SequenceEqual(File.ReadAllBytes(path)), "the file must be left untouched");
    }

    private static void BadFiles()
    {
        var path = TempFile();
        File.WriteAllText(path, "this is not json {{{");
        var before = File.ReadAllBytes(path);
        var v = NewVault(path);
        Expect(v.Open() == OpenResult.Unreadable, "garbage must be reported");
        Expect(before.SequenceEqual(File.ReadAllBytes(path)), "garbage must not be overwritten");

        var future = TempFile();
        File.WriteAllText(future, "{ \"Version\": 99, \"Mode\": \"dpapi\", \"Key\": \"AAAA\", \"Logins\": [] }");
        var futureBefore = File.ReadAllBytes(future);
        Expect(NewVault(future).Open() == OpenResult.TooNew, "a newer file must be reported");
        Expect(futureBefore.SequenceEqual(File.ReadAllBytes(future)), "a newer file must not be downgraded");

        var odd = TempFile();
        File.WriteAllText(odd, "{ \"Version\": 1, \"Mode\": \"nonsense\", \"Key\": \"AAAA\", \"Logins\": [] }");
        Expect(NewVault(odd).Open() == OpenResult.Unreadable, "an unknown mode is unreadable");
    }

    private static void DamagedBlocksSwitch()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        v.Add("https://example.com", "u", "p");

        var json = Read(path);
        json["Logins"]![0]!["Site"] = "https://other.example";
        Save(path, json);

        var again = NewVault(path);
        again.Open();
        var before = File.ReadAllBytes(path);
        Expect(again.UsePassword("a long enough password".AsSpan()) == VaultResult.Damaged, "a damaged entry must block the switch");
        Expect(before.SequenceEqual(File.ReadAllBytes(path)), "a refused switch must change nothing");
        Expect(again.Mode == VaultMode.Dpapi, "still in Dpapi mode");
    }

    private static void PasswordMode()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        var id = v.Add("https://example.com", "u", "the-secret");

        Expect(v.UsePassword("correct horse battery".AsSpan()) == VaultResult.Ok, "switch to password mode");
        Expect(v.Mode == VaultMode.Password && v.IsOpen, "password mode, still open this session");
        Expect(RevealText(v, id) == "the-secret", "entries survive the switch");

        var json = Read(path);
        Expect(json["Mode"]!.GetValue<string>() == "password", "file says password");
        Expect(json["Salt"] is not null && json["Iterations"]!.GetValue<int>() == Rounds, "salt and rounds are stored");

        // A new session: the key is not opened by Windows any more.
        var again = NewVault(path);
        Expect(again.Open() == OpenResult.NeedsPassword, "needs the password");
        Expect(!again.IsOpen, "shut until unlocked");
        Expect(again.Unlock("wrong wrong wrong".AsSpan()) == VaultResult.Wrong, "wrong password");
        Expect(!again.IsOpen, "still shut after a wrong password");
        Expect(again.Unlock("correct horse battery".AsSpan()) == VaultResult.Ok, "right password");
        Expect(RevealText(again, id) == "the-secret", "entry opens after unlock");

        // Even with the same Windows protection available, it does not open without the password.
        var anyKey = NewVault(path, new TestWrap(Fixed));
        anyKey.Open();
        Expect(!anyKey.IsOpen, "Windows protection alone must not open a password vault");

        again.Lock();
        Expect(!again.IsOpen, "lock forgets the key");
        ExpectThrows<InvalidOperationException>(() => again.List(), "a locked vault cannot be listed");
        Expect(again.Unlock("correct horse battery".AsSpan()) == VaultResult.Ok, "unlock again after lock");
    }

    private static void KeyIsReplaced()
    {
        var path = TempFile();
        var wrap = new TestWrap(Fixed);
        var v = NewVault(path, wrap);
        v.Open();
        var id = v.Add("https://example.com", "u", "the-secret");

        // What an old backup of the file would hold: the sealed old data key and the old entry.
        var oldFile = Read(path);
        var oldKey = wrap.Unprotect(Convert.FromBase64String(oldFile["Key"]!.GetValue<string>()))!;
        var oldPass = oldFile["Logins"]![0]!["Pass"]!.GetValue<string>();

        Expect(v.UsePassword("correct horse battery".AsSpan()) == VaultResult.Ok, "switch");
        var newPass = Read(path)["Logins"]![0]!["Pass"]!.GetValue<string>();
        Expect(newPass != oldPass, "the entry must be sealed again with the new key");

        // The old key, which Windows protection could still give to a program running as the
        // user, must not open what is in the file now.
        var aad = Aad.Field(id, "https://example.com", "pass");
        Expect(Seal.Decrypt(oldKey, Convert.FromBase64String(newPass), aad) is null, "the old data key opens the new file");
        Expect(Seal.Decrypt(oldKey, Convert.FromBase64String(oldPass), aad) is not null, "(sanity) the old key opens the old copy");
    }

    private static void ShortPassword()
    {
        var v = NewVault(TempFile());
        v.Open();
        Expect(v.UsePassword("short".AsSpan()) == VaultResult.TooShort, "too short");
        Expect(v.Mode == VaultMode.Dpapi, "nothing changed");

        Expect(v.UsePassword("long enough password".AsSpan()) == VaultResult.Ok, "long enough");
        Expect(v.ChangePassword("long enough password".AsSpan(), "tiny".AsSpan()) == VaultResult.TooShort, "change to a short one");
    }

    private static void Throttle()
    {
        var path = TempFile();
        var first = NewVault(path);
        first.Open();
        first.UsePassword("the right password".AsSpan());

        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var v = NewVault(path);
        v.Clock = () => now;
        v.Open();

        for (var i = 0; i < 3; i++)
            Expect(v.Unlock("wrong password 1".AsSpan()) == VaultResult.Wrong, "the first three wrong tries cost nothing");

        Expect(v.RetryAfter == TimeSpan.Zero, "no wait yet");
        Expect(v.Unlock("wrong password 1".AsSpan()) == VaultResult.Wrong, "fourth wrong try");
        Expect(v.RetryAfter > TimeSpan.Zero, "now there is a wait");

        // Even the right password is not tried during the wait.
        Expect(v.Unlock("the right password".AsSpan()) == VaultResult.Wait, "waiting");

        now = now.AddSeconds(3);
        Expect(v.RetryAfter == TimeSpan.Zero, "the wait is over");
        Expect(v.Unlock("the right password".AsSpan()) == VaultResult.Ok, "right password after the wait");

        // The wait grows and is capped.
        var w = NewVault(path);
        w.Clock = () => now;
        w.Open();
        var longest = TimeSpan.Zero;
        for (var i = 0; i < 20; i++)
        {
            now = now.AddMinutes(10);
            w.Unlock("wrong password 2".AsSpan());
            if (w.RetryAfter > longest)
                longest = w.RetryAfter;
        }

        Expect(longest <= TimeSpan.FromSeconds(60), "the wait must not pass a minute");
    }

    private static void ChangePassword()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        var id = v.Add("https://example.com", "u", "the-secret");
        v.UsePassword("old password here".AsSpan());

        Expect(v.ChangePassword("not the old one".AsSpan(), "brand new password".AsSpan()) == VaultResult.Wrong, "the current password is checked");
        Expect(v.ChangePassword("old password here".AsSpan(), "brand new password".AsSpan()) == VaultResult.Ok, "change");
        Expect(RevealText(v, id) == "the-secret", "entries survive the change");

        var again = NewVault(path);
        again.Open();
        Expect(again.Unlock("old password here".AsSpan()) == VaultResult.Wrong, "the old password no longer works");
        Expect(again.Unlock("brand new password".AsSpan()) == VaultResult.Ok, "the new password works");
        Expect(RevealText(again, id) == "the-secret", "entry opens");

        var dpapi = NewVault(TempFile());
        dpapi.Open();
        Expect(dpapi.ChangePassword("x".AsSpan(), "another password".AsSpan()) == VaultResult.Unavailable, "no password to change in Dpapi mode");
    }

    private static void BackToDpapi()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        var id = v.Add("https://example.com", "u", "the-secret");
        v.UsePassword("a password to leave".AsSpan());

        Expect(v.UseDpapi() == VaultResult.Ok, "back to Windows protection");
        var json = Read(path);
        Expect(json["Mode"]!.GetValue<string>() == "dpapi" && json["Salt"] is null, "file says dpapi and keeps no salt");

        var again = NewVault(path);
        Expect(again.Open() == OpenResult.Ready, "opens by itself again");
        Expect(RevealText(again, id) == "the-secret", "entry opens");

        var locked = NewVault(path);
        Expect(locked.UseDpapi() == VaultResult.Unavailable, "needs an open vault");
    }

    private static void UnicodeForms()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        v.UsePassword("Vi\u1ec7t Nam 2026".AsSpan());   // precomposed: one character for the letter with its marks

        var typedApart = "Vie\u0323\u0302t Nam 2026";    // letter, then separate marks
        var again = NewVault(path);
        again.Open();
        Expect(again.Unlock(typedApart.AsSpan()) == VaultResult.Ok, "the other form of the same password must unlock");
    }

    private static void UpdateRemove()
    {
        var path = TempFile();
        var v = NewVault(path);
        v.Open();
        var id = v.Add("https://one.example", "old-user", "old-pass");

        Expect(v.Update(id, "https://two.example/path", "new-user", null), "update with the password kept");
        Expect(v.List().Single().Site == "https://two.example" && v.List().Single().User == "new-user", "site and user changed");
        Expect(RevealText(v, id) == "old-pass", "a null password keeps the old one, even after the site changed");

        Expect(v.Update(id, "https://two.example", "new-user", "new-pass"), "update with a new password");
        Expect(RevealText(v, id) == "new-pass", "new password");

        Expect(!v.Update(Guid.NewGuid(), "https://x.example", "u", "p"), "unknown id");
        ExpectThrows<ArgumentException>(() => v.Update(id, "not an address", "u", "p"), "site must be a web address");
        ExpectThrows<ArgumentException>(() => v.Add("javascript:alert(1)", "u", "p"), "javascript: is not a site");

        Expect(v.Remove(id), "remove");
        Expect(!v.Remove(id), "remove twice");
        Expect(v.List().Count == 0, "empty again");
        Expect(v.Reveal(id, Good) is null, "revealing a removed entry gives nothing");

        var reopened = NewVault(path);
        reopened.Open();
        Expect(reopened.List().Count == 0, "the removal was written");
    }

    private static void FindExact()
    {
        var v = NewVault(TempFile());
        v.Open();
        v.Add("https://example.com", "a", "p");
        v.Add("https://example.com", "b", "p");
        v.Add("http://example.com", "plain", "p");
        v.Add("https://example.com:8443", "port", "p");
        v.Add("https://login.example.com", "sub", "p");

        Expect(v.Find("https://example.com/some/page?x=1").Select(i => i.User).SequenceEqual(new[] { "a", "b" }), "same origin only");
        Expect(v.Find("http://example.com").Single().User == "plain", "http is not https");
        Expect(v.Find("https://example.com:8443/").Single().User == "port", "the port counts");
        Expect(v.Find("https://example.com.evil.example").Count == 0, "a look-alike host");
        Expect(v.Find("https://evil.example/https://example.com").Count == 0, "a look-alike path");
        Expect(v.Find("https://xn--exmple-cua.com").Count == 0, "punycode look-alike");
        Expect(v.Find("not an address").Count == 0, "not an address");
    }

    private static void AtomicWrite()
    {
        var path = TempFile();

        // A leftover from a crash in the middle of an earlier write.
        File.WriteAllText(path + ".tmp", "half a file");

        var v = NewVault(path);
        v.Open();
        v.Add("https://example.com", "u", "p");
        v.UsePassword("a long enough password".AsSpan());

        Expect(!File.Exists(path + ".tmp"), "no temporary file is left behind");
        Expect(JsonNode.Parse(File.ReadAllText(path)) is not null, "the file is whole");

        // A write that cannot happen (the folder is a file) throws and changes nothing in memory.
        var blocked = TempFile();
        var folder = Path.GetDirectoryName(blocked)!;
        var w = NewVault(blocked);
        w.Open();
        var id = w.Add("https://example.com", "u", "p");
        Directory.Delete(folder, true);
        File.WriteAllText(folder, "now a file, not a folder");

        try
        {
            ExpectThrows<IOException>(() => w.Add("https://other.example", "u2", "p2"), "writing into a missing folder throws");
            Expect(w.List().Count == 1 && w.List()[0].Id == id, "a failed write must not leave the new entry in memory");
        }
        finally
        {
            File.Delete(folder);
        }
    }

    private static void Origins()
    {
        var cases = new (string? Url, string? Origin)[]
        {
            ("https://Example.com:443/a?b=c#d", "https://example.com"),
            ("http://example.com:80/", "http://example.com"),
            ("http://example.com:8080/x", "http://example.com:8080"),
            ("https://example.com./", "https://example.com"),
            ("  https://example.com/  ", "https://example.com"),
            ("http://user:pw@example.com/", "http://example.com"),
            ("https://b\u00fccher.de/", "https://xn--bcher-kva.de"),
            ("https://[::1]:3000/x", "https://[::1]:3000"),
            ("https://192.168.0.1/", "https://192.168.0.1"),
            ("ftp://example.com/", null),
            ("javascript:alert(1)", null),
            ("data:text/html,hi", null),
            ("file:///C:/x", null),
            ("about:blank", null),
            ("example.com", null),
            ("", null),
            (null, null),
        };

        foreach (var (url, expected) in cases)
        {
            var got = Origin.Of(url);
            Expect(got == expected, $"Origin.Of({url ?? "null"}) gave {got ?? "null"}, expected {expected ?? "null"}");
        }
    }

    private static void SecretWiped()
    {
        var bytes = new byte[] { 1, 2, 3, 4 };
        var secret = new Secret(bytes);
        Expect(secret.Bytes.Length == 4, "readable before dispose");

        secret.Dispose();
        Expect(bytes.All(b => b == 0), "the bytes are overwritten");
        ExpectThrows<ObjectDisposedException>(() => _ = secret.Bytes.Length, "a disposed secret cannot be read");
        secret.Dispose();
    }

    private static void NoLogging()
    {
        // The rule that no secret ever reaches a log is kept by keeping every kind of output
        // out of this folder, so there is nothing to forget to check. Comments are skipped.
        var folder = FindVaultSource();
        var banned = new[] { "Log.", "Console.", "Debug.", "Trace.", "Debugger.", "EventLog", "Print(" };
        var files = Directory.GetFiles(folder, "*.cs");
        Expect(files.Length >= 4, "the vault source files were not found in " + folder);

        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i].TrimStart();
                if (line.StartsWith("//"))
                    continue;

                foreach (var word in banned)
                    Expect(!line.Contains(word), $"{Path.GetFileName(file)}:{i + 1} uses {word}");
            }
        }
    }

    private static string FindVaultSource()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "neat", "vault");
            if (Directory.Exists(candidate))
                return candidate;

            dir = dir.Parent;
        }

        throw new Exception("src/neat/vault not found above " + AppContext.BaseDirectory);
    }

    private static void RealRounds()
    {
        var path = TempFile();
        var v = new Vault(path, new TestWrap(Fixed), Vault.DefaultIterations);
        v.Open();
        var id = v.Add("https://example.com", "u", "the-secret");

        var clock = Stopwatch.StartNew();
        Expect(v.UsePassword("a long enough password".AsSpan()) == VaultResult.Ok, "switch with the real round count");
        var one = clock.ElapsedMilliseconds;
        Expect(Read(path)["Iterations"]!.GetValue<int>() == 600_000, "600,000 rounds are written");

        var again = new Vault(path, new TestWrap(Fixed), Vault.DefaultIterations);
        again.Open();
        Expect(again.Unlock("a long enough password".AsSpan()) == VaultResult.Ok, "unlock with the real round count");
        Expect(RevealText(again, id) == "the-secret", "entry opens");

        Console.WriteLine($"        (one 600,000-round key took about {one} ms here)");
    }

    private static void RealDpapi()
    {
        var wrap = new Dpapi();
        var key = RandomNumberGenerator.GetBytes(32);

        var sealedKey = wrap.Protect(key);
        Expect(!sealedKey.SequenceEqual(key), "the sealed key must differ from the key");
        Expect(wrap.Unprotect(sealedKey)!.SequenceEqual(key), "round trip");
        Expect(wrap.Unprotect(RandomNumberGenerator.GetBytes(64)) is null, "garbage must not open");

        var tampered = (byte[])sealedKey.Clone();
        tampered[^1] ^= 0xFF;
        Expect(wrap.Unprotect(tampered) is null, "a changed block must not open");

        // And the whole vault on the real thing.
        var path = TempFile();
        var v = new Vault(path, new Dpapi(), Rounds);
        Expect(v.Open() == OpenResult.New, "new vault on real DPAPI");
        var id = v.Add("https://example.com", "u", "p");

        var again = new Vault(path, new Dpapi(), Rounds);
        Expect(again.Open() == OpenResult.Ready, "reopen on real DPAPI");
        Expect(RevealText(again, id) == "p", "entry opens on real DPAPI");
    }

    // ---- the gate ----

    private static (Vault Vault, string Path) PasswordVault(string password = "the right password")
    {
        var path = TempFile();
        var first = NewVault(path);
        first.Open();
        first.Add("https://example.com", "u", "the-secret");
        first.UsePassword(password.AsSpan());

        // A new session: shut until unlocked.
        var v = NewVault(path);
        v.Open();
        return (v, path);
    }

    private static void CheckPassword()
    {
        var (v, _) = PasswordVault();
        Expect(!v.IsOpen, "the vault starts shut");

        Expect(v.Check("the right password".AsSpan()) == VaultResult.Ok, "the right password checks out");
        Expect(!v.IsOpen, "checking must not open the vault");
        Expect(v.Check("a wrong password".AsSpan()) == VaultResult.Wrong, "a wrong password does not");

        // Wrong tries count, and the wait applies to Check as well as to Unlock.
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        v.Clock = () => now;
        for (var i = 0; i < 4; i++)
            v.Check("a wrong password".AsSpan());

        Expect(v.Check("the right password".AsSpan()) == VaultResult.Wait, "the wait applies to Check");
        now = now.AddMinutes(5);
        Expect(v.Check("the right password".AsSpan()) == VaultResult.Ok, "Check works again after the wait");

        var dpapi = NewVault(TempFile());
        dpapi.Open();
        Expect(dpapi.Check("anything at all".AsSpan()) == VaultResult.Unavailable, "no password to check in Dpapi mode");
        Expect(NewVault(TempFile()).Check("anything at all".AsSpan()) == VaultResult.Unavailable, "nothing to check before the vault is read");
    }

    private static void GateProtection()
    {
        var dpapi = NewVault(TempFile());
        dpapi.Open();

        var hello = new FakeHello();
        var gate = new Gate(dpapi, hello);

        foreach (var (state, expected) in new[]
        {
            (HelloState.Ready, Protection.Hello),
            (HelloState.NotSetUp, Protection.None),
            (HelloState.Blocked, Protection.None),
            (HelloState.NoDevice, Protection.None),
            (HelloState.Unknown, Protection.None),
        })
        {
            hello.State = state;
            Expect(gate.ProtectionAsync().Result == expected, $"Windows protection with Hello {state} should be {expected}");
            Expect(gate.Hello == state, "the gate keeps what Hello said");
        }

        // A password vault is protected by the password whatever Hello says.
        var (pw, _) = PasswordVault();
        hello.State = HelloState.NoDevice;
        Expect(new Gate(pw, hello).ProtectionAsync().Result == Protection.Password, "password vault, no Hello");
        hello.State = HelloState.Ready;
        Expect(new Gate(pw, hello).ProtectionAsync().Result == Protection.Password, "password vault, Hello ready");

        // Asking Hello fails: not usable, and no exception reaches the page.
        hello.ThrowOnState = true;
        Expect(gate.ProtectionAsync().Result == Protection.None, "a failing Hello check is not usable");
        Expect(gate.Hello == HelloState.Unknown, "and is reported as unknown");

        // A vault that was never read has nothing protecting it.
        hello.ThrowOnState = false;
        hello.State = HelloState.Ready;
        Expect(new Gate(NewVault(TempFile()), hello).ProtectionAsync().Result == Protection.None, "an unread vault");
    }

    private static void GateHello()
    {
        var v = NewVault(TempFile());
        v.Open();
        var hello = new FakeHello { State = HelloState.Ready, Answer = true };
        var gate = new Gate(v, hello);

        Expect(gate.Current is null, "no access at the start");
        Expect(gate.SignInAsync("Show the password for example.com").Result, "a yes from Hello opens an access");
        Expect(gate.Current is { Valid: true }, "and it is valid");
        Expect(hello.LastMessage == "Show the password for example.com", "Hello is shown the message");

        gate.SignOut();
        Expect(gate.Current is null, "sign out ends it");

        hello.Answer = false;
        Expect(!gate.SignInAsync("x").Result && gate.Current is null, "a no gives nothing");

        hello.ThrowOnAsk = true;
        Expect(!gate.SignInAsync("x").Result && gate.Current is null, "an error is a no, never a yes");

        // Hello is for Windows protection only, and only with the vault open.
        hello.ThrowOnAsk = false;
        hello.Answer = true;
        var (pw, _) = PasswordVault();
        Expect(!new Gate(pw, hello).SignInAsync("x").Result, "a shut password vault is not opened by Hello");

        // Even open (the password was typed this session), Hello is not the proof for a password vault.
        pw.Unlock("the right password".AsSpan());
        Expect(pw.IsOpen, "(sanity) the password vault is open");
        Expect(!new Gate(pw, hello).SignInAsync("x").Result, "an open password vault is not given access by Hello");

        // A vault whose key Windows will not open is not open, so there is nothing to give access to.
        var path = TempFile();
        NewVault(path).Open();
        var shut = NewVault(path, new TestWrap(RandomNumberGenerator.GetBytes(32)));
        shut.Open();
        Expect(!shut.IsOpen, "(sanity) the vault is shut");
        Expect(!new Gate(shut, hello).SignInAsync("x").Result, "a vault that is not open gives no access");
    }

    private static void GatePassword()
    {
        var (v, _) = PasswordVault();
        var gate = new Gate(v, new FakeHello());

        Expect(gate.SignIn("not the password".AsSpan()) == VaultResult.Wrong, "wrong password");
        Expect(gate.Current is null && !v.IsOpen, "a wrong password gives no access and leaves the vault shut");

        Expect(gate.SignIn("the right password".AsSpan()) == VaultResult.Ok, "right password");
        Expect(gate.Current is { Valid: true } && v.IsOpen, "an access, and the vault is open");

        // Open already: the password is only checked.
        gate.SignOut();
        Expect(!v.IsOpen, "sign out shuts a password vault");
        Expect(gate.SignIn("the right password".AsSpan()) == VaultResult.Ok, "sign in again");

        var dpapi = NewVault(TempFile());
        dpapi.Open();
        Expect(new Gate(dpapi, new FakeHello()).SignIn("x".AsSpan()) == VaultResult.Unavailable, "no password for a Windows-protected vault");
    }

    private static void GateSliding()
    {
        var v = NewVault(TempFile());
        v.Open();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var gate = new Gate(v, new FakeHello { State = HelloState.Ready, Answer = true }) { Clock = () => now };

        gate.SignInAsync("x").Wait();
        var access = gate.Current!;

        now = now.AddMinutes(2);
        Expect(access.Valid, "still valid after 2 minutes");
        gate.Touch();

        now = now.AddMinutes(2);
        Expect(access.Valid, "4 minutes after the start but 2 after the last use: still valid");

        now = now.AddMinutes(1.5);
        Expect(!access.Valid && gate.Current is null, "3 minutes after the last use it has run out");

        // A dead access stays dead.
        gate.Touch();
        Expect(!access.Valid, "touching a dead access does not bring it back");
    }

    private static void GateTickAndSignOut()
    {
        // Password vault: the end of the access shuts the vault.
        var (v, _) = PasswordVault();
        var now = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var gate = new Gate(v, new FakeHello()) { Clock = () => now };
        gate.SignIn("the right password".AsSpan());

        Expect(!gate.Tick(), "nothing to end while the access is valid");
        now = now.AddMinutes(4);
        Expect(gate.Tick(), "the access has run out");
        Expect(!v.IsOpen && gate.Current is null, "the vault is shut and the access is gone");
        Expect(!gate.Tick(), "it says so only once");

        // Windows-protected vault: the vault stays open, only the access ends.
        var dpapi = NewVault(TempFile());
        dpapi.Open();
        var id = dpapi.Add("https://example.com", "u", "p");
        var clock = new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        var g2 = new Gate(dpapi, new FakeHello { State = HelloState.Ready, Answer = true }) { Clock = () => clock };
        g2.SignInAsync("x").Wait();
        var old = g2.Current!;

        clock = clock.AddMinutes(4);
        Expect(g2.Tick() && dpapi.IsOpen, "the access ends, the Windows-protected vault stays open");
        ExpectThrows<UnauthorizedAccessException>(() => dpapi.Reveal(id, old), "the old access no longer reveals");

        g2.SignInAsync("x").Wait();
        var fresh = g2.Current!;
        g2.SignOut();
        ExpectThrows<UnauthorizedAccessException>(() => dpapi.Reveal(id, fresh), "sign out ends the access at once");
        Expect(dpapi.IsOpen, "sign out leaves a Windows-protected vault open");
    }

    private static void GateReveal()
    {
        var v = NewVault(TempFile());
        v.Open();
        var id = v.Add("https://example.com", "u", "the-secret");

        var gate = new Gate(v, new FakeHello { State = HelloState.Ready, Answer = true });
        Expect(gate.Current is null, "no access, nothing to reveal with");
        gate.SignInAsync("x").Wait();

        using (var secret = v.Reveal(id, gate.Current!))
            Expect(secret!.Text() == "the-secret", "revealed through the gate's access");

        // Setting a password: the person just typed it twice, so an access is granted.
        Expect(v.UsePassword("a fresh password".AsSpan()) == VaultResult.Ok, "switch to a password");
        gate.SignOut();
        Expect(!v.IsOpen, "the password vault is shut by sign out");
        gate.SignIn("a fresh password".AsSpan());
        using (var secret = v.Reveal(id, gate.Current!))
            Expect(secret!.Text() == "the-secret", "revealed after the password");

        var granted = NewVault(TempFile());
        granted.Open();
        var g3 = new Gate(granted, new FakeHello());
        g3.Grant();
        Expect(g3.Current is { Valid: true }, "grant starts an access");
    }

    private sealed class FakeHello : IHello
    {
        public HelloState State { get; set; } = HelloState.Ready;

        public bool Answer { get; set; }

        public bool ThrowOnState { get; set; }

        public bool ThrowOnAsk { get; set; }

        public string? LastMessage { get; private set; }

        public Task<HelloState> StateAsync()
        {
            return ThrowOnState ? throw new InvalidOperationException("no Hello") : Task.FromResult(State);
        }

        public Task<bool> AskAsync(string message)
        {
            LastMessage = message;
            return ThrowOnAsk ? throw new InvalidOperationException("no Hello") : Task.FromResult(Answer);
        }
    }

    // ---- a stand-in for DPAPI that works anywhere ----

    private sealed class TestWrap : IKeyWrap
    {
        private readonly byte[] _secret;

        public TestWrap(byte[] secret)
        {
            _secret = secret;
        }

        public byte[] Protect(byte[] key)
        {
            return Seal.Encrypt(_secret, key, Array.Empty<byte>());
        }

        public byte[]? Unprotect(byte[] blob)
        {
            return Seal.Decrypt(_secret, blob, Array.Empty<byte>());
        }
    }
}
