namespace ResXManager.Translators;

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Composition;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Runtime.Serialization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;
using ResXManager.Infrastructure;
using TomsToolbox.Essentials;
using TomsToolbox.Wpf.Composition.AttributedModel;

[DataTemplate(typeof(YoudaoBulkTranslator))]
public class YoudaoBulkTranslatorConfiguration : Decorator
{
}

[Export(typeof(ITranslator)), Shared]
public class YoudaoBulkTranslator : TranslatorBase
{
    private static readonly Uri _uri = new("https://ai.youdao.com/DOCSIRMA/html/trans/api/wbfy/index.html");

    private static readonly Dictionary<string, string> _youdaoCultureMap = new()
    {
        { "ja-JP", "ja"},
        //{ "zh-Hant", "zh-CHT"},
        //{ "zh-CHT","zh-CHT"},
        //{ "zh-TW","zh-CHT"},
        //{ "zh-HK","zh-CHT"},
        //{ "zh-MO","zh-CHT"},
        //{ "zh-Hans","zh-CHS"},
        //{ "zh-CN","zh-CHS"},
        { "vi-VN","vi"},
        { "es-MX","es"},
    };

    private static readonly IList<ICredentialItem> _credentialItems = new ICredentialItem[]
    {
        new CredentialItem("appKey", "API Key"),
        new CredentialItem("appSecret", "Secret Key"),
        new CredentialItem("ApiUrl", "Api Url", false),
    };

    public YoudaoBulkTranslator()
        : base("YoudaoBulk", "YoudaoBulk", _uri, _credentialItems)
    {
    }

    [DataMember(Name = "appKey")]
    public string? SerializedAppKey
    {
        get => SaveCredentials ? Credentials[0].Value : null;
        set => Credentials[0].Value = value;
    }

    [DataMember(Name = "appSecret")]
    public string? SerializedAppSecret
    {
        get => SaveCredentials ? Credentials[1].Value : null;
        set => Credentials[1].Value = value;
    }

    /// <summary>
    /// "https://openapi.youdao.com/v2/api"//批量文本翻译
    /// </summary>
    [DataMember(Name = "ApiUrl")]
    [DefaultValue("https://openapi.youdao.com/v2/api")]
    public string? ApiUrl
    {
        get => Credentials[2].Value;
        set => Credentials[2].Value = value;
    }

    private string? appKey => Credentials[0].Value;
    private string? AppSecret => Credentials[1].Value;

    protected override async Task Translate(ITranslationSession translationSession)
    {
        if (appKey.IsNullOrEmpty())
        {
            translationSession.AddMessage("YoudaoBulk Translator requires APP Key.");
            return;
        }
        if (AppSecret.IsNullOrEmpty())
        {
            translationSession.AddMessage("Youdao Translator requires App Secret.");
            return;
        }
        foreach (var languageGroup in translationSession.Items.GroupBy(item => item.TargetCulture))
        {
            if (translationSession.IsCanceled)
                break;

            var targetCulture = languageGroup.Key.Culture ?? translationSession.NeutralResourcesLanguage;

            using var itemsEnumerator = languageGroup.GetEnumerator();
            while (true)
            {
                var sourceItems = itemsEnumerator.Take(10);
                if (translationSession.IsCanceled || !sourceItems.Any())
                    break;

                // Build out list of parameters
                var parameters = new List<string?>(30);

                var ts = DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);
                var millis = (long)ts.TotalMilliseconds;
                var curtime = Convert.ToString(millis / 1000);

                parameters.AddRange(new[] { "curtime", curtime });

                var salt = DateTime.Now.Millisecond.ToString();
                var signStr = appKey + Truncate(string.Join("", sourceItems.Select(p => p.Source))) + salt + curtime + AppSecret; ;
                var sign = ComputeHash(signStr, new SHA256CryptoServiceProvider());

                foreach (var item in sourceItems)
                {
                    parameters.AddRange(new[] { "q", WebUtility.UrlEncode(item.Source) });
                }

                parameters.AddRange(new[]
                {
                    "from", YoudaoBulkLangCode(translationSession.SourceLanguage),
                    "to", YoudaoBulkLangCode(targetCulture),
                    "signType", "v3",
                    "appKey", appKey,
                    "salt", salt,
                    "sign", sign,
                    "strict", "true",
                });


                var apiUrl = ApiUrl;
                if (apiUrl.IsNullOrWhiteSpace())
                {
                    apiUrl = "https://openapi.youdao.com/v2/api";
                }

                // Call the YoudaoBulk API
                var response = await GetHttpResponse<YoudaoBulkTranslationResponse>(
                    apiUrl,
                    parameters,
                    translationSession.CancellationToken).ConfigureAwait(false);

                if (response.ErrorCode != "0")
                {
                    translationSession.AddMessage("YoudaoBulk Translator  Error " + response.ErrorCode + ":" + GetErrorName(response.ErrorCode) + ",row Index:" + string.Join(",", response.ErrorIndex ?? Array.Empty<int>()));
                    return;
                }
                await translationSession.MainThread.StartNew(() =>
                {
                    if (response.TranslateResults == null) return;
                    foreach (var tuple in sourceItems.Zip(response.TranslateResults ?? Array.Empty<TranslateResult>(),
                                 (a, b) => new Tuple<ITranslationItem, string?>(a, b.Translation)))
                    {
                        tuple.Item1.Results.Add(new TranslationMatch(this, tuple.Item2, Ranking));
                    }
                }).ConfigureAwait(false);
            }
        }
    }

    protected static string ComputeHash(string input, HashAlgorithm algorithm)
    {
        var inputBytes = Encoding.UTF8.GetBytes(input);
        var hashedBytes = algorithm.ComputeHash(inputBytes);
        return BitConverter.ToString(hashedBytes).Replace("-", "");
    }

    protected static string? Truncate(string q)
    {
        if (q == null)
        {
            return null;
        }
        var len = q.Length;
        return len <= 20 ? q : (q.Substring(0, 10) + len + q.Substring(len - 10, 10));
    }

    private static string YoudaoBulkLangCode(CultureInfo cultureInfo)
    {
        var iso1 = cultureInfo.TwoLetterISOLanguageName;
        var name = cultureInfo.Name;

        if (string.Equals(iso1, "zh", StringComparison.OrdinalIgnoreCase))
            return new[] { "zh-hant", "zh-cht", "zh-hk", "zh-mo", "zh-tw" }.Contains(name, StringComparer.OrdinalIgnoreCase) ? "zh-CHT" : "zh-CHS";

        if (_youdaoCultureMap.TryGetValue(name, out var langName))
            return langName;

        return iso1;
    }

    private static async Task<T> GetHttpResponse<T>(string baseUrl, ICollection<string?> parameters, CancellationToken cancellationToken)
        where T : class
    {
        var url = BuildUrl(baseUrl, parameters);

        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(new Uri(url), cancellationToken).ConfigureAwait(false);

        response.EnsureSuccessStatusCode();

#pragma warning disable CA2016 // Forward the 'CancellationToken' parameter to methods => not available in NetFramework
        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        return JsonConverter<T>(stream) ?? throw new InvalidOperationException("Empty response.");
    }

    private static T? JsonConverter<T>(Stream stream)
        where T : class
    {
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return JsonConvert.DeserializeObject<T>(reader.ReadToEnd());
    }

    [DataContract]
    private sealed class TranslateResult
    {
        [DataMember(Name = "query")]
        public string? Query { get; set; }

        [DataMember(Name = "translation")]
        public string? Translation { get; set; }

        [DataMember(Name = "type")]
        public string? Type { get; set; }
    }

    [DataContract]
    private sealed class YoudaoBulkTranslationResponse
    {
        [DataMember(Name = "errorCode")]
        public string ErrorCode { get; set; } = "";

        [DataMember(Name = "errorIndex")]
        public int[]? ErrorIndex { get; set; }

        [DataMember(Name = "translateResults")]
        public TranslateResult[]? TranslateResults { get; set; }
    }

    /// <summary>Builds the URL from a base, method name, and name/value paired parameters. All parameters are encoded.</summary>
    /// <param name="url">The base URL.</param>
    /// <param name="pairs">The name/value paired parameters.</param>
    /// <returns>Resulting URL.</returns>
    /// <exception cref="ArgumentException">There must be an even number of strings supplied for parameters.</exception>
    private static string BuildUrl(string url, ICollection<string?> pairs)
    {
        if (pairs.Count % 2 != 0)
            throw new ArgumentException("There must be an even number of strings supplied for parameters.");

        var sb = new StringBuilder(url);
        if (pairs.Count > 0)
        {
            sb.Append('?');
            sb.Append(string.Join("&", pairs.Where((s, i) => i % 2 == 0).Zip(pairs.Where((s, i) => i % 2 == 1), Format)));
        }
        return sb.ToString();

        static string Format(string? a, string? b)
        {
            return string.Concat(a, "=", b);
        }
    }

    private static string GetErrorName(string errorCode)
    {
        return _errorCodeMap.TryGetValue(errorCode, out var errorName) ? errorName : "Unknown Error";
    }

    private static readonly Dictionary<string, string> _errorCodeMap = new()
    {
        { "102","不支持的语言类型"},
        { "103","翻译文本过长"},
        { "202", "签名检验失败,如果确认应用ID和应用密钥的正确性，仍返回202，一般是编码问题。请确保翻译文本 q 为UTF-8编码."},
        { "207","重放请求"},
        { "302","翻译查询失败"},
        { "303","服务端的其它异常"},
        { "304","翻译失败，请联系技术同学"},
        { "401","账户已经欠费，请进行账户充值"},
        { "411", "访问频率受限,请稍后访问"},
        { "412","长请求过于频繁，请稍后访问"},
    };
}
