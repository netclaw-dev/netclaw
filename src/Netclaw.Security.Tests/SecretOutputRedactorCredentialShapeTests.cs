// -----------------------------------------------------------------------
// <copyright file="SecretOutputRedactorCredentialShapeTests.cs" company="Petabridge, LLC">
//      Copyright (C) 2026 - 2026 Petabridge, LLC <https://petabridge.com>
// </copyright>
// -----------------------------------------------------------------------
using Xunit;

namespace Netclaw.Security.Tests;

/// <summary>
/// Secret shapes from issue #2595: URL credentials, option values, secret headers, name and
/// value pairs, and secret key names. Each shape has positive cases and negative cases.
/// Over-redaction has a cost. The model reads redacted tool results, the operator reads the
/// redacted approval display, and memory curation drops any text that
/// <see cref="SecretOutputRedactor.ContainsSecretLikeContent"/> flags.
/// </summary>
public sealed class SecretOutputRedactorCredentialShapeTests
{
    private const string Mask = "***REDACTED***";

    // The secrets are separated by '|'. The kept parts are separated by '|' too.
    // Each secret part must be gone, and each kept part must stay.
    private static void AssertMasked(string input, string secrets, string kept)
    {
        var redacted = SecretOutputRedactor.Redact(input);

        Assert.Contains(Mask, redacted, StringComparison.Ordinal);
        foreach (var secret in secrets.Split('|', StringSplitOptions.RemoveEmptyEntries))
            Assert.DoesNotContain(secret, redacted, StringComparison.Ordinal);
        foreach (var part in kept.Split('|', StringSplitOptions.RemoveEmptyEntries))
            Assert.Contains(part, redacted, StringComparison.Ordinal);
        Assert.True(SecretOutputRedactor.ContainsSecretLikeContent(input));
    }

    private static void AssertUnchanged(string input)
    {
        Assert.Equal(input, SecretOutputRedactor.Redact(input));
        Assert.False(SecretOutputRedactor.ContainsSecretLikeContent(input));
    }

    // ── URL credentials ──
    // The password in the userinfo part of a URL is a secret. The user name can stay.
    // The scheme, the host, the port, and the path must stay, so the model can still use the URL.

    [Theory]
    [InlineData("https://bob:hunter2@host/x", "hunter2", "https://|@host/x")]
    [InlineData("postgres://u:p%40ss@db:5432/app", "p%40ss", "postgres://|@db:5432/app")]
    [InlineData("git clone https://user:ghp_xxx@github.com/o/r", "ghp_xxx", "git clone https://|@github.com/o/r")]
    [InlineData("redis://:hunter2@cache:6379/0", "hunter2", "redis://|@cache:6379/0")]
    [InlineData("mongodb+srv://admin:s3cr3tPass@cluster0.example.net/app?retryWrites=true", "s3cr3tPass", "mongodb+srv://|@cluster0.example.net/app")]
    [InlineData("amqp://guest:rabbitPw9@rabbit:5672/vhost", "rabbitPw9", "amqp://|@rabbit:5672/vhost")]
    [InlineData("""{"url": "https://bob:hunter2@host/x"}""", "hunter2", "@host/x")]
    // RFC 3986 lets the password contain ':'. All of it is the secret.
    [InlineData("https://bob:pa55:w0rd@host/x", "pa55|w0rd", "@host/x")]
    public void Redact_masks_the_password_in_url_credentials(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // Each case has an '@' or a ':' near a URL, but no password in a userinfo part.
    [Theory]
    [InlineData("https://host/x")]
    [InlineData("https://bob@host/x")]
    [InlineData("mailto:bob@example.com")]
    [InlineData("meet at 12:30@site")]
    [InlineData("email:bob@example.com")]
    [InlineData("git@github.com:netclaw-dev/netclaw.git")]
    [InlineData("ssh://git@github.com:2222/o/r.git")]
    [InlineData("http://localhost:4873/@myorg/pkg")]
    [InlineData("https://host:8443/path@v2")]
    [InlineData("https://example.com/search?q=from:bob@example.com")]
    [InlineData("http://[::1]:8080/health")]
    [InlineData("docker://registry.example.com:5000/app@sha256:2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824")]
    public void Redact_leaves_urls_without_a_password_alone(string input)
        => AssertUnchanged(input);

    // ── Option values ──
    // A secret-named option followed by a value. The option name and the other arguments stay.

    [Theory]
    [InlineData("mysql -u root --password hunter2 -h db.example.com", "hunter2", "mysql -u root --password|-h db.example.com")]
    [InlineData("vault login --token s.abc123tok", "s.abc123tok", "vault login --token")]
    [InlineData("tool --api-key abc123key --region eu", "abc123key", "tool --api-key|--region eu")]
    [InlineData("az login --service-principal -u app-id --client-secret abc123sec --tenant t1", "abc123sec", "--client-secret|--tenant t1")]
    [InlineData("tool --access-token abc123acc", "abc123acc", "tool --access-token")]
    [InlineData("deploy --admin-password hunter2 --region eu", "hunter2", "deploy --admin-password|--region eu")]
    [InlineData("tool --password \"correct horse battery\" --verbose", "correct|horse|battery", "--password|--verbose")]
    public void Redact_masks_a_secret_option_value_after_a_space(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // The '=' form is already masked by the name rule. These cases keep that behavior.
    [Theory]
    [InlineData("mysql --password=hunter2", "hunter2", "mysql --")]
    [InlineData("tool --api-key=abc123key", "abc123key", "tool --")]
    [InlineData("tool --client-secret=abc123sec", "abc123sec", "tool --")]
    [InlineData("tool --token=abc123tok", "abc123tok", "tool --")]
    public void Redact_masks_a_secret_option_value_after_an_equals_sign(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // An option that names a file, a flag that takes no value, and a flag at the end of a line
    // have no secret value. '-p' is a port, a path flag, or a database name for many tools, so
    // the redactor cannot know when '-p' takes a password. Help text shows placeholders, not secrets.
    [Theory]
    [InlineData("tool --password-file /run/secrets/db")]
    [InlineData("tool --token-file /run/secrets/tok")]
    [InlineData("pg_dump --no-password mydb")]
    [InlineData("mysql -u root --password")]
    [InlineData("mysql -u root --password\nmysql> SHOW DATABASES;")]
    [InlineData("echo \"$PW\" | docker login --username bob --password-stdin registry.example.com")]
    [InlineData("docker run -p 8080:80 nginx")]
    [InlineData("ssh -p 22 admin@host")]
    [InlineData("mysql -u root -p mydb")]
    [InlineData("mkdir -p /var/lib/app")]
    [InlineData("  --password PASSWORD   The password for the user")]
    [InlineData("  --token <token>       An API token")]
    public void Redact_leaves_options_without_a_secret_value_alone(string input)
        => AssertUnchanged(input);

    // ── Headers ──
    // A header that carries a credential. The header name and the text after the header stay.

    [Theory]
    [InlineData("curl -H \"X-Api-Key: abc123key\" https://api.example.com/v1", "abc123key", "X-Api-Key|https://api.example.com/v1")]
    [InlineData("curl -H 'X-Api-Key:abc123key' https://api.example.com/v1", "abc123key", "X-Api-Key|https://api.example.com/v1")]
    [InlineData("> x-api-key: abc123key\n> accept: */*", "abc123key", "x-api-key|> accept: */*")]
    [InlineData("api-key: abc123key", "abc123key", "api-key")]
    [InlineData("X-Auth-Token: tok7f3a9c\nX-Request-Id: 42", "tok7f3a9c", "X-Auth-Token|X-Request-Id: 42")]
    [InlineData("X-Vault-Token: hvs.CAESIabc123", "hvs.CAESIabc123", "X-Vault-Token")]
    [InlineData("Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz", "Authorization")]
    [InlineData("Authorization: token abc123def", "abc123def", "Authorization")]
    [InlineData("Proxy-Authorization: Basic dXNlcjpwYXNz", "dXNlcjpwYXNz", "Proxy-Authorization")]
    [InlineData("Cookie: session=abc123sess; theme=dark", "abc123sess", "Cookie")]
    [InlineData("< Set-Cookie: session=abc123sess; Path=/; HttpOnly", "abc123sess", "Set-Cookie")]
    [InlineData("""{"headers": {"X-Api-Key": "abc123key", "Accept": "application/json"}}""", "abc123key", "X-Api-Key|\"Accept\": \"application/json\"")]
    public void Redact_masks_secret_header_values(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // A header that names a credential, or that has a scheme but no credential, is not a secret.
    [Theory]
    [InlineData("X-Request-Id: 123")]
    [InlineData("Content-Type: application/json")]
    [InlineData("Accept: */*")]
    [InlineData("X-RateLimit-Remaining: 59")]
    [InlineData("WWW-Authenticate: Bearer realm=\"api\"")]
    [InlineData("Access-Control-Allow-Headers: Authorization, X-Api-Key, Content-Type")]
    [InlineData("Cookie policy: essential cookies only")]
    public void Redact_leaves_ordinary_headers_alone(string input)
        => AssertUnchanged(input);

    // ── Name and value pairs ──
    // HAR files and Postman collections keep a header or a variable as a name and a value.

    [Theory]
    [InlineData("""[{"name":"Authorization","value":"Bearer abc123tok"}]""", "abc123tok", "\"name\":\"Authorization\"")]
    [InlineData("""{"key":"api_key","value":"abc123key"}""", "abc123key", "\"key\":\"api_key\"")]
    [InlineData("""{ "name": "X-Api-Key", "value": "abc123key" }""", "abc123key", "\"name\": \"X-Api-Key\"")]
    [InlineData("""{"key":"password","value":"hunter2","type":"secret"}""", "hunter2", "\"key\":\"password\"|\"type\":\"secret\"")]
    public void Redact_masks_the_value_of_a_secret_name_value_pair(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    [Theory]
    [InlineData("""{"name":"Content-Type","value":"text/plain"}""")]
    [InlineData("""[{"name":"Accept","value":"application/json"},{"name":"page","value":"2"}]""")]
    [InlineData("""{"name":"next_page_token","value":"CAESBwoF"}""")]
    public void Redact_leaves_ordinary_name_value_pairs_alone(string input)
        => AssertUnchanged(input);

    // ── Secret key names ──
    // IsSecretKey hides an argument value in the approval display. It also stops
    // "netclaw doctor --fix" from a delete of an unknown config property.

    [Theory]
    [InlineData("passwd")]
    [InlineData("db_passwd")]
    [InlineData("admin_pwd")]
    [InlineData("db_pwd")]
    [InlineData("passphrase")]
    [InlineData("ssh_passphrase")]
    [InlineData("cookie")]
    [InlineData("Cookie")]
    [InlineData("cookies")]
    [InlineData("session_cookie")]
    [InlineData("set-cookie")]
    [InlineData("Set-Cookie")]
    [InlineData("x-api-key")]
    public void IsSecretKey_matches_secret_key_names(string key)
        => Assert.True(SecretOutputRedactor.IsSecretKey(key), key);

    // These names start with a short secret word, but their values are not secrets.
    // A plain substring rule for "pwd" or "cookie" would hide them. PWD and OLDPWD are the
    // shell variables that hold the working directory, so a name that is exactly one of them
    // is not secret. The text rule of Redact does not know "pwd" either: "PWD=/work" stays.
    [Theory]
    [InlineData("PWD")]
    [InlineData("pwd")]
    [InlineData("Pwd")]
    [InlineData("OLDPWD")]
    [InlineData("oldpwd")]
    [InlineData("pwd_dir")]
    [InlineData("cookie_consent")]
    [InlineData("cookie_domain")]
    [InlineData("cookie_path")]
    [InlineData("cookie_jar")]
    public void IsSecretKey_leaves_names_that_only_start_with_a_short_secret_word(string key)
        => Assert.False(SecretOutputRedactor.IsSecretKey(key), key);

    // Redact and IsSecretKey share one vocabulary. A passphrase value in text is masked too.
    [Theory]
    [InlineData("""{"passphrase": "correct-horse-9"}""", "correct-horse-9")]
    [InlineData("GPG_PASSPHRASE=correct-horse-9", "correct-horse-9")]
    public void Redact_masks_passphrase_values(string input, string secret)
        => AssertMasked(input, secret, "");

    // ── Review findings for the first fix (#2595) ──

    // A password can hold '@'. A user name can hold '@' too (an e-mail address). The userinfo
    // part ends at the last '@' before the host, so all of the password must be masked.
    [Theory]
    [InlineData("mongodb://admin:p@ssw0rd!@db:27017/app", "ssw0rd|p@ss", "mongodb://|@db:27017/app")]
    [InlineData("https://bob:p@ss@host/x", "p@ss|ss@host", "https://|@host/x")]
    [InlineData("https://bob@corp.com:hunter2@git.example.com/x", "hunter2", "https://|@git.example.com/x")]
    public void Redact_masks_a_url_password_that_holds_an_at_sign(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // Prose, docs tables, and help text name a secret option. The next word is not a value.
    // A false mask here drops a memory and changes help output that the model reads.
    [Theory]
    [InlineData("--token must be a PAT with repo scope.")]
    [InlineData("Use --password-file instead of --password because it keeps it safe")]
    [InlineData("use the --token option.")]
    [InlineData("Set --token (or GH_TOKEN) before you run it.")]
    [InlineData("pass --token `<your token>` here")]
    [InlineData("| --password | the password |")]
    [InlineData("      --token string           Bearer token for authentication to the API server")]
    [InlineData("      --client-secret string   The OAuth client secret")]
    [InlineData("  -p, --password string        Password for the registry")]
    [InlineData("--session-token (string)")]
    [InlineData("aws s3api list-objects-v2 --bucket b --next-token CAESBwoF")]
    [InlineData("aws s3api list-objects --bucket b --starting-token eyJNYXJrZXIiOiBudWxsfQ")]
    public void Redact_leaves_prose_and_help_text_after_a_secret_option_alone(string input)
        => AssertUnchanged(input);

    // A header-like line that names a token, a pagination cursor, or a counter is not a secret.
    [Theory]
    [InlineData("Token: required for all calls")]
    [InlineData("token: expired, please refresh")]
    [InlineData("Next-Token: CAESBwoF")]
    [InlineData("X-RateLimit-Token: 5")]
    [InlineData("input-token: 500")]
    [InlineData("""{"next-token": "CAESBwoF"}""")]
    [InlineData("""{"Next-Token": "CAESBwoF"}""")]
    [InlineData("""{"X-RateLimit-Token": "5"}""")]
    [InlineData("""{"input-token": "500"}""")]
    public void Redact_leaves_token_named_lines_that_hold_no_secret_alone(string input)
        => AssertUnchanged(input);

    // Headers that carry a credential in forms that the first fix did not know:
    // no space after the colon, a wget option, a hyphenated scheme, and other header names.
    [Theory]
    [InlineData("curl -H X-Api-Key:abc123key https://api.example.com/v1", "abc123key", "X-Api-Key|https://api.example.com/v1")]
    [InlineData("wget --header=X-Api-Key:abc123key https://example.com/f", "abc123key", "X-Api-Key|https://example.com/f")]
    [InlineData("Authorization: Api-Key abc123key", "abc123key", "Authorization: Api-Key")]
    [InlineData("Ocp-Apim-Subscription-Key: abc123key", "abc123key", "Ocp-Apim-Subscription-Key")]
    [InlineData("X-Auth-Key: abc123key", "abc123key", "X-Auth-Key")]
    [InlineData("X-Client-Secret: abc123sec", "abc123sec", "X-Client-Secret")]
    [InlineData("""{"Ocp-Apim-Subscription-Key": "abc123key"}""", "abc123key", "\"Ocp-Apim-Subscription-Key\"")]
    [InlineData("""{"X-Client-Secret": "abc123sec"}""", "abc123sec", "\"X-Client-Secret\"")]
    [InlineData("""{"Set-Cookie": "session=abc123sess; Path=/"}""", "abc123sess", "\"Set-Cookie\"")]
    [InlineData("""{"Proxy-Authorization": "Basic dXNlcjpwYXNz"}""", "dXNlcjpwYXNz", "\"Proxy-Authorization\"")]
    public void Redact_masks_more_secret_header_forms(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // The pair rule and IsSecretKey use one vocabulary: a secret key name is a secret pair name.
    [Theory]
    [InlineData("MYSQL_PWD")]
    [InlineData("db_pwd")]
    [InlineData("admin_pwd")]
    [InlineData("passwd")]
    [InlineData("ssh_passphrase")]
    [InlineData("session_cookie")]
    [InlineData("set-cookie")]
    [InlineData("x-api-key")]
    public void Redact_masks_a_pair_whose_name_is_a_secret_key(string name)
    {
        Assert.True(SecretOutputRedactor.IsSecretKey(name), name);
        AssertMasked($$"""{"name":"{{name}}","value":"hunter2"}""", "hunter2", $"\"{name}\"");
    }

    // A counter, or the shell working directory, is not a secret pair.
    [Theory]
    [InlineData("""{"name":"max_tokens","value":"4096"}""")]
    [InlineData("""{"name":"token_count","value":"5"}""")]
    [InlineData("""{"name":"PWD","value":"/work"}""")]
    [InlineData("""{"name":"OLDPWD","value":"/home/u"}""")]
    public void Redact_leaves_counter_and_directory_pairs_alone(string input)
        => AssertUnchanged(input);

    // A variable reference, a template placeholder, or a literal flag is not a secret. Each new
    // rule keeps it, so a script or a Postman collection stays readable. The prefixed name rule
    // ignores case for %x%, so the new rules ignore case too.
    [Theory]
    [InlineData("tool --password $DB_PASS")]
    [InlineData("tool --password \"$DB_PASS\"")]
    [InlineData("tool --password %DB_PASS%")]
    [InlineData("tool --password '%DB_PASS%'")]
    [InlineData("tool --password %db_pass%")]
    [InlineData("tool --password {{db_pass}}")]
    [InlineData("tool --password \"{{ db_pass }}\"")]
    [InlineData("tool --token \"<token>\"")]
    [InlineData("tool --token true")]
    [InlineData("X-Api-Key: $API_KEY")]
    [InlineData("curl -H \"X-Api-Key: %API_KEY%\" https://api.example.com")]
    [InlineData("X-Api-Key: {{api_key}}")]
    [InlineData("Authorization: Basic $CREDS")]
    [InlineData("Authorization: Basic {{creds}}")]
    [InlineData("""{"X-Api-Key": "$API_KEY"}""")]
    [InlineData("""{"X-Api-Key": "{{api_key}}"}""")]
    [InlineData("""{"X-Api-Key": "%API_KEY%"}""")]
    [InlineData("""{"name":"api_key","value":"{{api_key}}"}""")]
    [InlineData("""{"key":"password","value":"$DB_PASS"}""")]
    [InlineData("""{"name":"api_key","value":"true"}""")]
    [InlineData("https://bob:$PASSWORD@host/x")]
    [InlineData("https://bob:${PASSWORD}@host/x")]
    [InlineData("https://bob:%PASSWORD%@host/x")]
    [InlineData("https://bob:{{password}}@host/x")]
    public void Redact_leaves_references_and_placeholders_in_the_new_rules_alone(string input)
        => AssertUnchanged(input);

    // ── Re-review findings for the second fix (#2595) ──

    // A weak password is often one lower-case word. Words after it are more arguments, not a
    // sentence, so the value is masked. The prose cases above must still stay unchanged.
    [Theory]
    [InlineData("mysql -u root --password changeme mydb", "changeme", "mysql -u root --password|mydb")]
    [InlineData("docker login --password secret registry.example.com", "secret registry", "docker login --password|registry.example.com")]
    [InlineData("mysqladmin --password secret status", "secret status", "mysqladmin --password|status")]
    [InlineData("helm repo add --password hunter myrepo https://charts.example.com", "hunter", "--password|myrepo https://charts.example.com")]
    [InlineData("kubectl --token abcdefghij get pods", "abcdefghij", "kubectl --token|get pods")]
    public void Redact_masks_a_weak_option_password_that_looks_like_a_word(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // A lower-case header value with words after it is still a value. A JSON value under a
    // secret header key is never prose: a passphrase can hold spaces.
    // "token: expired, please refresh" and "Token: required for all calls" stay unchanged (above).
    [Theory]
    [InlineData("X-Api-Key: abcdefghijkl sent upstream", "abcdefghijkl", "X-Api-Key|sent upstream")]
    [InlineData("X-Auth-Token: hunter retry later", "hunter", "X-Auth-Token|retry later")]
    [InlineData("token: abcdefgh more text", "abcdefgh", "token|more text")]
    [InlineData("""{"X-Auth-Token": "correct horse battery staple"}""", "correct|horse|battery|staple", "\"X-Auth-Token\"")]
    public void Redact_masks_a_lower_case_header_value_that_words_follow(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // The descriptor check compares whole name segments (split at '_', '-', and a case change).
    // "secret_account" ends in the segment "account", not "count". "secretname" is one segment.
    // A plural is a credential list unless a counter word comes before it (max_tokens).
    // An Authorization header held under a "...Header" name is the header value.
    [Theory]
    [InlineData("""{"name":"AuthorizationHeader","value":"Bearer abc123"}""", "abc123", "\"AuthorizationHeader\"")]
    [InlineData("""{"name":"api_tokens","value":"abc123"}""", "abc123", "\"api_tokens\"")]
    [InlineData("""{"name":"refresh_tokens","value":"abc123"}""", "abc123", "\"refresh_tokens\"")]
    [InlineData("""{"name":"secret_account","value":"abc123"}""", "abc123", "\"secret_account\"")]
    [InlineData("""{"name":"secretname","value":"abc123"}""", "abc123", "\"secretname\"")]
    public void Redact_masks_pairs_whose_name_is_secret_by_segment(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // A last segment that is a descriptor, or a plural after a counter word, is not a secret.
    [Theory]
    [InlineData("""{"name":"secret_name","value":"prod/jwt"}""")]
    [InlineData("""{"name":"SecretName","value":"prod/jwt"}""")]
    [InlineData("""{"name":"next_page_token","value":"CAESBwoF"}""")]
    [InlineData("""{"name":"prompt_tokens","value":"1000"}""")]
    [InlineData("""{"name":"completion_tokens","value":"234"}""")]
    public void Redact_leaves_pairs_whose_last_segment_is_a_descriptor_or_a_count_alone(string input)
        => AssertUnchanged(input);

    // The old redactor masked these names: "starting" was not a pagination word. The superset
    // rule (SecretOutputRedactorLinearTimeTests) keeps all old masks. The option form
    // "--starting-token x" stays unchanged (above).
    [Theory]
    [InlineData("STARTING_TOKEN=abc123", "abc123", "STARTING_TOKEN=")]
    [InlineData("AWS_STARTING_TOKEN=abc123", "abc123", "AWS_STARTING_TOKEN=")]
    public void Redact_masks_starting_token_env_values_as_the_old_redactor_did(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // ── Third review findings (#2595) ──

    // ';' after an option value ends a shell command. It is not sentence punctuation, so the
    // value is masked, as it is before "&&". A prose word is short: a long run of lower-case
    // letters before a period is a value. "use the --token option." stays unchanged (above).
    [Theory]
    [InlineData("mysql -u root --password changeme; echo done", "changeme", "mysql -u root --password|echo done")]
    [InlineData("mysql -u root --password changeme && ls", "changeme", "mysql -u root --password|&& ls")]
    [InlineData("--token abcdefghijklmnopqrstuvwxyzabcdef.", "abcdefghijklmnopqrstuvwxyzabcdef", "--token")]
    [InlineData("Run with --token qwertyuiopasdfghjklzxcvbnm.", "qwertyuiopasdfghjklzxcvbnm", "Run with --token")]
    [InlineData("X-Api-Key: abcdefghijklmnopqrstuvwxyzabcdef.", "abcdefghijklmnopqrstuvwxyzabcdef", "X-Api-Key")]
    public void Redact_masks_a_value_before_a_shell_separator_or_a_long_word_before_a_period(string input, string secrets, string kept)
        => AssertMasked(input, secrets, kept);

    // A value that is itself a function word or a verb of a sentence is prose. The weak
    // passwords (changeme, secret, hunter, abcdefghij, abcdefghijkl, abcdefgh) stay masked (above).
    [Theory]
    [InlineData("--token should come from the vault.")]
    [InlineData("--token can be omitted when GITHUB_TOKEN is set.")]
    [InlineData("--api-key was rotated last week.")]
    [InlineData("--api-key takes precedence over the config file.")]
    [InlineData("Remember: --api-key takes precedence over the env var.")]
    [InlineData("Prefer --token over --password.")]
    [InlineData("token: the session token")]
    [InlineData("\"token: the session token\"")]
    public void Redact_leaves_a_sentence_whose_next_word_is_a_function_word_alone(string input)
        => AssertUnchanged(input);
}

/// <summary>
/// Redact must not allocate far more than its input. A header, JSON key, or option name of
/// 4 MB is adversarial input. The allocation counter is per thread, and Redact runs on the
/// test thread, so other tests do not change the count.
/// </summary>
[Collection(RedactorTimingCollection.Name)]
public sealed class SecretOutputRedactorCredentialShapeAllocationTests
{
    private const int AdversarialChars = 4 * 1024 * 1024;

    // A few copies of the input are normal: each rule that matches makes a new string.
    private const int MaxCopiesOfInput = 4;

    private static string Rep(string unit) => string.Concat(Enumerable.Repeat(unit, AdversarialChars / unit.Length));

    private static IEnumerable<(string Name, Func<string> Build)> Shapes()
    {
        yield return ("long header name", () => Rep("a-") + "token: abc123key");
        yield return ("long json header key", () => "{\"" + Rep("a-") + "token\": \"abc123key\"}");
        yield return ("long option name", () => "--" + Rep("a-") + "token abc123key");
    }

    public static TheoryData<string> ShapeNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Shapes())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_allocates_at_most_a_few_copies_of_a_long_name(string shape)
    {
        var input = Shapes().Single(s => s.Name == shape).Build();
        SecretOutputRedactor.Redact(input[..1024]); // regex construction is a one-off cost, not the subject here

        var before = GC.GetAllocatedBytesForCurrentThread();
        SecretOutputRedactor.Redact(input);
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        long inputBytes = input.Length * sizeof(char);
        Assert.True(allocated <= MaxCopiesOfInput * inputBytes,
            $"{shape}: Redact allocated {allocated / 1024 / 1024} MB for {inputBytes / 1024 / 1024} MB of input (limit {MaxCopiesOfInput} copies)");
    }
}

/// <summary>
/// The patterns for issue #2595 must stay linear. Each input is adversarial for one pattern:
/// a long run of a prefix with no end. The work of a linear pattern for 4 MB is well below the
/// budget. A quadratic pattern needs minutes.
/// </summary>
[Collection(RedactorTimingCollection.Name)]
public sealed class SecretOutputRedactorCredentialShapeLinearTimeTests
{
    private const int AdversarialChars = 4 * 1024 * 1024;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);

    private static string Rep(string unit) => string.Concat(Enumerable.Repeat(unit, Math.Max(1, AdversarialChars / unit.Length)));

    private static IEnumerable<(string Name, Func<string> Build)> Shapes()
    {
        yield return ("url scheme and user with no '@'", () => Rep("https://a:"));
        yield return ("one scheme then user parts with no '@'", () => "https://" + Rep("a:"));
        yield return ("one scheme then a long user", () => "https://" + new string('a', AdversarialChars));
        yield return ("password option repeats", () => Rep("--password "));
        yield return ("password option then spaces", () => "--password" + new string(' ', AdversarialChars));
        yield return ("long option name", () => "--" + Rep("token-"));
        yield return ("api key header names", () => Rep("X-Api-Key:"));
        yield return ("api key header then spaces", () => "X-Api-Key: " + new string(' ', AdversarialChars));
        yield return ("cookie pairs", () => "Cookie: " + Rep("a=b; "));
        yield return ("basic auth then a long value", () => "Authorization: Basic " + new string('a', AdversarialChars));
        yield return ("name with no value", () => Rep("{\"name\":\"Authorization\","));
        yield return ("name then an open value", () => "{\"name\":\"Authorization\",\"value\":\"" + new string('a', AdversarialChars));
        yield return ("url user then many '@'", () => "://a:" + Rep("b@"));
        yield return ("dense option matches", () => Rep("--token x "));
        yield return ("dense header lines", () => Rep("X-Api-Key: a\n"));
        yield return ("dense json header keys", () => "{" + Rep("\"X-Api-Key\": \"a\", "));
        yield return ("json header key then an open value", () => "{\"X-Api-Key\": \"" + new string('a', AdversarialChars));
        yield return ("long hyphenated json key", () => "{\"" + Rep("a-"));
        yield return ("closed json header value of words", () => "{\"X-Auth-Token\": \"" + Rep("a ") + "\"}");
        yield return ("closed json header value of one word", () => "{\"X-Auth-Token\": \"" + new string('a', AdversarialChars) + "\"}");
        yield return ("closed pair value of words", () => "{\"name\":\"api_key\",\"value\":\"" + Rep("a ") + "\"}");
        yield return ("closed pair value of one word", () => "{\"name\":\"api_key\",\"value\":\"" + new string('a', AdversarialChars) + "\"}");
    }

    public static TheoryData<string> ShapeNames()
    {
        var data = new TheoryData<string>();
        foreach (var (name, _) in Shapes())
            data.Add(name);
        return data;
    }

    [Theory]
    [MemberData(nameof(ShapeNames))]
    public void Redact_finishes_adversarial_credential_shapes_within_budget(string shape)
    {
        var input = Shapes().Single(s => s.Name == shape).Build();
        SecretOutputRedactor.Redact(input[..1024]); // regex construction is a one-off cost, not the subject here

        // A quadratic pattern cannot be interrupted, so run it on a background thread and stop waiting.
        var worker = new Thread(() => SecretOutputRedactor.Redact(input)) { IsBackground = true };
        worker.Start();
        var finished = worker.Join(Budget);

        Assert.True(finished, $"{shape}: {AdversarialChars / 1024 / 1024} MB did not finish within {Budget.TotalSeconds} s");
    }

    // A long run of unfinished URL credentials must not hide a real one after it.
    [Fact]
    public void Redact_masks_url_credentials_that_follow_a_run_of_unfinished_ones()
    {
        var input = string.Concat(Enumerable.Repeat("https://a:", 200)) + " https://bob:hunter2@host/x tail";

        var redacted = SecretOutputRedactor.Redact(input);

        Assert.DoesNotContain("hunter2", redacted, StringComparison.Ordinal);
        Assert.EndsWith("@host/x tail", redacted, StringComparison.Ordinal);
    }
}
