using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net;
using System.Text;
using System.Web.Script.Serialization;

namespace StickyNotes
{
    // Small JSON helpers on top of the serializer that ships with .NET Framework.
    static class Json
    {
        static JavaScriptSerializer NewSerializer()
        {
            var serializer = new JavaScriptSerializer();
            serializer.MaxJsonLength = int.MaxValue;
            return serializer;
        }

        // Throws on malformed text; returns null when the JSON is valid but not an object.
        public static Dictionary<string, object> Parse(string json)
        {
            return NewSerializer().DeserializeObject(json) as Dictionary<string, object>;
        }

        public static string Write(object value)
        {
            return NewSerializer().Serialize(value);
        }

        public static string Str(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as string : null;
        }

        public static Dictionary<string, object> Obj(Dictionary<string, object> d, string key)
        {
            object v;
            return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
        }

        // Google sends 64-bit numbers as strings, so accept both.
        public static long Long(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0;
            string s = v as string;
            if (s != null)
            {
                long parsed;
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out parsed) ? parsed : 0;
            }
            try { return Convert.ToInt64(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0; }
        }

        public static float Float(Dictionary<string, object> d, string key)
        {
            object v;
            if (d == null || !d.TryGetValue(key, out v) || v == null) return 0f;
            try { return Convert.ToSingle(v, CultureInfo.InvariantCulture); }
            catch (Exception) { return 0f; }
        }

        public static List<Dictionary<string, object>> Objects(Dictionary<string, object> d, string key)
        {
            var list = new List<Dictionary<string, object>>();
            object v;
            var items = d != null && d.TryGetValue(key, out v) ? v as IList : null;
            if (items != null)
            {
                foreach (object item in items)
                {
                    var o = item as Dictionary<string, object>;
                    if (o != null) list.Add(o);
                }
            }
            return list;
        }
    }

    // A response from Google with an error status code.
    class HttpError : Exception
    {
        public readonly int Status;
        public readonly string Body;

        public HttpError(int status, string body)
            : base(Describe(status, body))
        {
            Status = status;
            Body = body ?? "";
        }

        // The OAuth error code, e.g. "invalid_grant". Null for other kinds of errors.
        public string OAuthError
        {
            get
            {
                try { return Json.Str(Json.Parse(Body), "error"); }
                catch (Exception) { return null; }
            }
        }

        static string Describe(int status, string body)
        {
            string detail = null;
            try
            {
                // OAuth: {"error": "code", "error_description": "..."}; APIs: {"error": {"message": "..."}}
                Dictionary<string, object> root = Json.Parse(body);
                Dictionary<string, object> apiError = Json.Obj(root, "error");
                detail = apiError != null
                    ? Json.Str(apiError, "message")
                    : Json.Str(root, "error_description") ?? Json.Str(root, "error");
            }
            catch (Exception) { }
            return "Google 回應錯誤（HTTP " + status + "）" + (string.IsNullOrEmpty(detail) ? "" : "：" + detail);
        }
    }

    static class Http
    {
        // Sends a request and returns the response body. Throws HttpError for error
        // status codes and WebException when Google can't be reached at all.
        public static string Request(string method, string url, string bearerToken, string contentType, byte[] body)
        {
            var request = (HttpWebRequest)WebRequest.Create(url);
            request.Method = method;
            request.Timeout = 30000;
            request.ReadWriteTimeout = 30000;
            request.UserAgent = "DesktopStickyNotes/1.0";
            request.AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate;
            if (bearerToken != null)
                request.Headers[HttpRequestHeader.Authorization] = "Bearer " + bearerToken;

            if (body != null)
            {
                request.ContentType = contentType;
                request.ContentLength = body.Length;
                using (Stream s = request.GetRequestStream())
                    s.Write(body, 0, body.Length);
            }
            else if (method == "POST" || method == "PATCH")
            {
                request.ContentLength = 0;
            }

            try
            {
                using (var response = (HttpWebResponse)request.GetResponse())
                    return ReadBody(response);
            }
            catch (WebException ex)
            {
                var response = ex.Response as HttpWebResponse;
                if (response == null) throw;
                int status = (int)response.StatusCode;
                string text;
                using (response)
                {
                    try { text = ReadBody(response); }
                    catch (IOException) { text = ""; }
                }
                throw new HttpError(status, text);
            }
        }

        static string ReadBody(HttpWebResponse response)
        {
            using (Stream s = response.GetResponseStream())
            using (var reader = new StreamReader(s, Encoding.UTF8))
                return reader.ReadToEnd();
        }

        public static string Escape(string value)
        {
            return Uri.EscapeDataString(value ?? "");
        }
    }
}
