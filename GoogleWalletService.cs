using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Creates and updates Google Wallet LoyaltyObjects.
///
/// Setup required (one-time, ~10 minutes):
///   1. Go to pay.google.com/business/console → sign up as an issuer (free).
///      Copy your numeric Issuer ID.
///   2. Open Google Cloud Console → create or pick a project → enable the
///      "Google Wallet API".
///   3. Create a Service Account → grant it the "Wallet Object Issuer" role →
///      create a JSON key → download it.
///   4. In the Loyalty Admin → Settings: paste the Issuer ID and the full
///      contents of the JSON key file.
///   5. The first earn/redeem on any customer's card will create their
///      LoyaltyClass (once) and LoyaltyObject, and push the "Save to Google
///      Wallet" link to their card page.
/// </summary>
public class GoogleWalletService
{
    private readonly string _issuerId;
    private readonly string _serviceAccountEmail;
    private readonly RSA _rsa;
    private const string ClassSuffix = "loyalty_card";
    private static readonly HttpClient Http = new();

    public GoogleWalletService(string issuerId, string serviceAccountJsonKey)
    {
        _issuerId = issuerId;
        var sa = JObject.Parse(serviceAccountJsonKey);
        _serviceAccountEmail = sa["client_email"]!.Value<string>()!;

        // Load the RSA private key from the service account JSON
        var pem = sa["private_key"]!.Value<string>()!
            .Replace("\\n", "\n")
            .Replace("-----BEGIN RSA PRIVATE KEY-----", "")
            .Replace("-----END RSA PRIVATE KEY-----", "")
            .Replace("-----BEGIN PRIVATE KEY-----", "")
            .Replace("-----END PRIVATE KEY-----", "")
            .Replace("\n", "")
            .Trim();

        _rsa = RSA.Create();
        try { _rsa.ImportPkcs8PrivateKey(Convert.FromBase64String(pem), out _); }
        catch { _rsa.ImportRSAPrivateKey(Convert.FromBase64String(pem), out _); }
    }

    // -----------------------------------------------------------------------
    // JWT helpers
    // -----------------------------------------------------------------------
    private string ServiceAccountJwt(string scope)
    {
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object>
        {
            ["iss"] = _serviceAccountEmail,
            ["sub"] = _serviceAccountEmail,
            ["aud"] = "https://oauth2.googleapis.com/token",
            ["iat"] = now,
            ["exp"] = now + 3600,
            ["scope"] = scope
        };
        return SignJwt(payload);
    }

    private string SignJwt(Dictionary<string, object> payload)
    {
        var header = Base64UrlEncode("{\"alg\":\"RS256\",\"typ\":\"JWT\"}");
        var body = Base64UrlEncode(System.Text.Json.JsonSerializer.Serialize(payload));
        var signing = $"{header}.{body}";
        var sig = _rsa.SignData(Encoding.UTF8.GetBytes(signing), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        return $"{signing}.{Base64UrlEncode(sig)}";
    }

    private static string Base64UrlEncode(string s) => Base64UrlEncode(Encoding.UTF8.GetBytes(s));
    private static string Base64UrlEncode(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private async Task<string> GetAccessToken()
    {
        var jwt = ServiceAccountJwt("https://www.googleapis.com/auth/wallet_object.issuer");
        var resp = await Http.PostAsync("https://oauth2.googleapis.com/token",
            new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = jwt
            }));
        var body = await resp.Content.ReadAsStringAsync();
        return JObject.Parse(body)["access_token"]!.Value<string>()!;
    }

    private string ClassId => $"{_issuerId}.{ClassSuffix}";
    private string ObjectId(string customerToken) => $"{_issuerId}.loyalty_{customerToken}";

    // -----------------------------------------------------------------------
    // LoyaltyClass — created once per programme, holds the programme template
    // -----------------------------------------------------------------------
    public async Task EnsureClassExists(string issuerName, string programName, string baseUrl)
    {
        var token = await GetAccessToken();
        var classId = ClassId;
        var check = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"https://walletobjects.googleapis.com/walletobjects/v1/loyaltyClass/{Uri.EscapeDataString(classId)}")
        { Headers = { Authorization = new("Bearer", token) } });

        var loyaltyClass = new
        {
            id = classId,
            issuerName,
            programName,
            programLogo = new { sourceUri = new { uri = $"{baseUrl}/logo.jpg" }, contentDescription = new { defaultValue = new { language = "en-GB", value = "Logo" } } },
            rewardsTierLabel = "Points",
            rewardsTier = "Member",
            reviewStatus = "UNDER_REVIEW",
            hexBackgroundColor = "#094582"
            // heroImage intentionally omitted — it was rendering at QR-code scale on the pass
        };

        if (check.IsSuccessStatusCode)
        {
            // PUT (full replace) so omitting heroImage actually removes it from the existing class
            await Http.SendAsync(new HttpRequestMessage(HttpMethod.Put,
                $"https://walletobjects.googleapis.com/walletobjects/v1/loyaltyClass/{Uri.EscapeDataString(classId)}")
            {
                Headers = { Authorization = new("Bearer", token) },
                Content = new StringContent(JsonConvert.SerializeObject(loyaltyClass), Encoding.UTF8, "application/json")
            });
            return;
        }

        await Http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
            "https://walletobjects.googleapis.com/walletobjects/v1/loyaltyClass")
        {
            Headers = { Authorization = new("Bearer", token) },
            Content = new StringContent(JsonConvert.SerializeObject(loyaltyClass), Encoding.UTF8, "application/json")
        });
    }

    // -----------------------------------------------------------------------
    // LoyaltyObject — one per customer, holds their name + points balance
    // -----------------------------------------------------------------------
    public async Task UpsertObject(string customerToken, string customerName, long points, string shopName,
        IReadOnlyList<(string header, string body)>? stampModules = null)
    {
        var token = await GetAccessToken();
        var objectId = ObjectId(customerToken);
        var classId = ClassId;

        var textModules = BuildTextModules(shopName, stampModules);
        var loyaltyPoints = BuildLoyaltyPoints(points, stampModules);

        var obj = new
        {
            id = objectId,
            classId = classId,
            state = "ACTIVE",
            accountId = customerToken,
            accountName = customerName,
            loyaltyPoints,
            barcode = new
            {
                type = "QR_CODE",
                value = customerToken,
                alternateText = customerToken
            },
            textModulesData = textModules
        };

        var json = JsonConvert.SerializeObject(obj);
        var check = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"https://walletobjects.googleapis.com/walletobjects/v1/loyaltyObject/{Uri.EscapeDataString(objectId)}")
        { Headers = { Authorization = new("Bearer", token) } });

        if (check.IsSuccessStatusCode)
        {
            // PATCH: update points and stamp card text together
            var patch = new { loyaltyPoints, textModulesData = textModules };
            var patchResp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Patch,
                $"https://walletobjects.googleapis.com/walletobjects/v1/loyaltyObject/{Uri.EscapeDataString(objectId)}")
            {
                Headers = { Authorization = new("Bearer", token) },
                Content = new StringContent(JsonConvert.SerializeObject(patch), Encoding.UTF8, "application/json")
            });
            if (!patchResp.IsSuccessStatusCode)
            {
                var errBody = await patchResp.Content.ReadAsStringAsync();
                throw new Exception($"PATCH loyaltyObject {(int)patchResp.StatusCode}: {errBody}");
            }
        }
        else
        {
            await Http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
                "https://walletobjects.googleapis.com/walletobjects/v1/loyaltyObject")
            {
                Headers = { Authorization = new("Bearer", token) },
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });
        }
    }

    // -----------------------------------------------------------------------
    // "Save to Google Wallet" JWT — what the card page button opens
    // -----------------------------------------------------------------------
    public string SaveUrl(string customerToken, string customerName, long points, string shopName,
        IReadOnlyList<(string header, string body)>? stampModules = null)
    {
        var objectId = ObjectId(customerToken);
        var classId = ClassId;

        var obj = new
        {
            id = objectId,
            classId = classId,
            state = "ACTIVE",
            accountId = customerToken,
            accountName = customerName,
            loyaltyPoints = BuildLoyaltyPoints(points, stampModules),
            barcode = new
            {
                type = "QR_CODE",
                value = customerToken,
                alternateText = customerToken
            },
            textModulesData = BuildTextModules(shopName, stampModules)
        };

        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var payload = new Dictionary<string, object>
        {
            ["iss"] = _serviceAccountEmail,
            ["aud"] = "google",
            ["typ"] = "savetowallet",
            ["iat"] = now,
            ["payload"] = new { loyaltyObjects = new[] { obj } }
        };
        var jwt = SignJwt(payload);
        return $"https://pay.google.com/gp/v/save/{jwt}";
    }

    public bool IsConfigured => !string.IsNullOrEmpty(_issuerId) && _rsa is not null;

    // Returns the raw JSON of the customer's loyalty object on Google's servers (for diagnostics)
    public async Task<(int status, string body)> GetObjectRaw(string customerToken)
    {
        var token = await GetAccessToken();
        var objectId = ObjectId(customerToken);
        var resp = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"https://walletobjects.googleapis.com/walletobjects/v1/loyaltyObject/{Uri.EscapeDataString(objectId)}")
        { Headers = { Authorization = new("Bearer", token) } });
        return ((int)resp.StatusCode, await resp.Content.ReadAsStringAsync());
    }

    // loyaltyPoints field: if stamps exist, show "🏆🏆○○○" as the big balance + status as the label.
    // This is the most reliably visible field on the pass — textModulesData may not render on all devices.
    private static object BuildLoyaltyPoints(long points, IReadOnlyList<(string header, string body)>? stampModules)
    {
        if (stampModules != null && stampModules.Count > 0)
        {
            var parts = stampModules[0].body.Split('\n');
            var grid = parts[0];                                          // e.g. "⚽○○○○○○○○○"
            var status = parts.Length > 1 ? parts[1] : stampModules[0].header; // e.g. "1 of 10 coffees"
            return new { balance = new { @string = grid }, label = status };
        }
        return new { balance = new { @string = points.ToString() }, label = "Points" };
    }

    private static object[] BuildTextModules(string shopName, IReadOnlyList<(string header, string body)>? stampModules)
    {
        var modules = new List<object>
        {
            new { id = "programme", header = "Programme", body = $"{shopName} Loyalty" }
        };
        if (stampModules != null)
        {
            int i = 0;
            foreach (var (h, b) in stampModules)
                modules.Add(new { id = $"stamp_{i++}", header = h, body = b });
        }
        return modules.ToArray();
    }

    // -----------------------------------------------------------------------
    // Diagnostic — returns raw API responses so we can see exactly what's failing
    // -----------------------------------------------------------------------
    public async Task<object> TestConnection(string shopName, string baseUrl)
    {
        // Step 1: get access token
        string accessToken;
        try { accessToken = await GetAccessToken(); }
        catch (Exception ex) { return new { step = "get_access_token", error = ex.Message }; }

        // Step 2: check if LoyaltyClass exists
        var classId = ClassId;
        var getClass = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Get,
            $"https://walletobjects.googleapis.com/walletobjects/v1/loyaltyClass/{Uri.EscapeDataString(classId)}")
        { Headers = { Authorization = new("Bearer", accessToken) } });
        var getClassBody = await getClass.Content.ReadAsStringAsync();

        if (!getClass.IsSuccessStatusCode)
        {
            // Step 3: try to create it
            var loyaltyClass = new
            {
                id = classId,
                issuerName = shopName,
                programName = $"{shopName} Loyalty",
                reviewStatus = "UNDER_REVIEW"
            };
            var createClass = await Http.SendAsync(new HttpRequestMessage(HttpMethod.Post,
                "https://walletobjects.googleapis.com/walletobjects/v1/loyaltyClass")
            {
                Headers = { Authorization = new("Bearer", accessToken) },
                Content = new StringContent(JsonConvert.SerializeObject(loyaltyClass), Encoding.UTF8, "application/json")
            });
            var createClassBody = await createClass.Content.ReadAsStringAsync();
            return new
            {
                step = "create_class",
                classId,
                getStatus = (int)getClass.StatusCode,
                createStatus = (int)createClass.StatusCode,
                createResponse = createClassBody
            };
        }

        return new { step = "class_exists", classId, status = (int)getClass.StatusCode };
    }
}
