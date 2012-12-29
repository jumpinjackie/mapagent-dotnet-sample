using OSGeo.MapGuide;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;

namespace MyCustomMapAgentHandler
{
    /// <summary>
    /// Summary description for mapagent
    /// </summary>
    public class mapagent : IHttpHandler
    {
        public void ProcessRequest(HttpContext context)
        {
            MapGuideApi.MgInitializeWebTier("C:\\Program Files\\OSGeo\\MapGuide\\Web\\www\\webconfig.ini");

            //mapagent only accepts GET or POST, so reject unsupported methods
            bool isGet = context.Request.HttpMethod == "GET";
            bool isPost = context.Request.HttpMethod == "POST";
            if (!isGet && !isPost)
            {
                context.Response.StatusCode = 400;
                context.Response.Write("Unsupported method: " + context.Request.HttpMethod);
                return;
            }

            //We need the current request url as the mapagent may need to reference this URL for certain operations
            //(for example: GetMapKml/GetLayerKml/GetFeaturesKml)
            String uri = context.Request.RawUrl;
            try
            {
                //This is the workhorse behind the mapagent handler, the previously mysterious MgHttpRequest class
                MgHttpRequest request = new MgHttpRequest(uri);

                //MgHttpRequestParam is the set of key/value parameter pairs that you need to set up for the
                //MgHttpRequest instance. We extract the relevant parameters from the HttpContext and pass it
                //down
                MgHttpRequestParam param = request.GetRequestParam();

                //Extract any parameters from the http authentication header if there is one
                bool bGotAuth = ParseAuthenticationHeader(param, context);

                if (isGet)
                {
                    PopulateGetRequest(param, context);
                }
                else if (isPost)
                {
                    PopulatePostRequest(param, context);
                }

                //A request is valid if it contains any of the following:
                //
                // 1. A SESSION parameter
                // 2. A USERNAME parameter (PASSWORD optional). If not specified the http authentication header is checked and extracted if found
                //
                //Whether these values are valid will be determined by MgSiteConnection in the MgHttpRequest handler when we come to execute it
                bool bValid = param.ContainsParameter("SESSION");
                if (!bValid)
                    bValid = param.ContainsParameter("USERNAME");

                if (!bValid)
                {
                    HandleUnauthorized(context);
                    return;
                }

                SendRequest(request, context);
            }
            catch (MgException ex)
            {
                HandleMgException(ex, context);
            }
            catch (Exception ex)
            {
                HandleException(ex, context);
            }
        }

        private bool ParseAuthenticationHeader(MgHttpRequestParam param, HttpContext context)
        {
            String auth = context.Request.Headers["authorization"];
            if (auth != null && auth.Length > 6)
            {
                auth = auth.Substring(6);
                byte[] decoded = Convert.FromBase64String(auth);
                String decodedStr = Encoding.UTF8.GetString(decoded);
                String[] decodedTokens = decodedStr.Split(':');
                if (decodedTokens.Length == 1 || decodedTokens.Length == 2)
                {
                    String username = decodedTokens[0];
                    String password = "";
                    if (decodedTokens.Length == 2)
                        password = decodedTokens[1];

                    param.AddParameter("USERNAME", username);
                    param.AddParameter("PASSWORD", password);
                    return true;
                }
            }
            return false;
        }

        private static void HandleMgHttpError(MgHttpResult result, HttpContext context)
        {
            String statusMessage = result.GetHttpStatusMessage();
            if (statusMessage.Equals("MgAuthenticationFailedException") || statusMessage.Equals("MgUnauthorizedAccessException"))
            {
                HandleUnauthorized(context);
            }
            else
            {
                String errHtml = String.Format(
                    "\r\n" +
                    "<html>\n<head>\n" +
                    "<title>{0}</title>\n" +
                    "<meta http-equiv=\"Content-Type\" content=\"text/html; charset=utf-8\">\n" +
                    "</head>\n" +
                    "<body>\n<h2>{1}</h2>\n{2}\n</body>\n</html>\n",
                    statusMessage,
                    result.GetErrorMessage(),
                    result.GetDetailedErrorMessage());
                context.Response.ContentType = "text/html";
                context.Response.Write(errHtml);
            }
        }

        private static void SendRequest(MgHttpRequest request, HttpContext context)
        {
            MgHttpRequestParam param = request.GetRequestParam();
            MgHttpResponse response = request.Execute();
            MgHttpResult result = response.GetResult();

            context.Response.StatusCode = result.GetStatusCode();
            if (context.Response.StatusCode == 200)
            {
                //MgDisposable is MapGuide's "object" class, so we need to do type
                //testing to find out the underlying derived type. The list of expected
                //types is small, so there isn't too much of this checking to do
                MgDisposable resultObj = result.GetResultObject();
                if (resultObj != null)
                {
                    context.Response.ContentType = result.GetResultContentType();
                    MgByteReader outputReader = null;
                    if (resultObj is MgByteReader)
                    {
                        outputReader = (MgByteReader)resultObj;
                        OutputReaderContent(context, outputReader);
                    }
                    else if (resultObj is MgFeatureReader)
                    {
                        outputReader = ((MgFeatureReader)resultObj).ToXml();
                        OutputReaderContent(context, outputReader);
                    }
                    else if (resultObj is MgSqlDataReader)
                    {
                        outputReader = ((MgSqlDataReader)resultObj).ToXml();
                        OutputReaderContent(context, outputReader);
                    }
                    else if (resultObj is MgDataReader)
                    {
                        outputReader = ((MgDataReader)resultObj).ToXml();
                        OutputReaderContent(context, outputReader);
                    }
                    else if (resultObj is MgStringCollection)
                    {
                        outputReader = ((MgStringCollection)resultObj).ToXml();
                        OutputReaderContent(context, outputReader);
                    }
                    else if (resultObj is MgSpatialContextReader)
                    {
                        outputReader = ((MgSpatialContextReader)resultObj).ToXml();
                        OutputReaderContent(context, outputReader);
                    }
                    else if (resultObj is MgLongTransactionReader)
                    {
                        outputReader = ((MgSpatialContextReader)resultObj).ToXml();
                        OutputReaderContent(context, outputReader);
                    }
                    else if (resultObj is MgHttpPrimitiveValue)
                    {
                        context.Response.Write(((MgHttpPrimitiveValue)resultObj).ToString());
                    }
                    else //Shouldn't get here
                    {
                        context.Response.StatusCode = 400;
                        context.Response.Write("Not sure how to output: " + resultObj.ToString());
                    }
                }
                else
                {
                    //The operation may not return any content at all
                }
            }
            else
            {
                HandleMgHttpError(result, context);
            }
        }

        private static void OutputReaderContent(HttpContext context, MgByteReader outputReader)
        {
            using (MemoryStream memBuf = new MemoryStream())
            {
                byte[] byteBuffer = new byte[1024];
                int numBytes = outputReader.Read(byteBuffer, 1024);
                while (numBytes > 0)
                {
                    memBuf.Write(byteBuffer, 0, numBytes);
                    numBytes = outputReader.Read(byteBuffer, 1024);
                }
                byte[] content = memBuf.ToArray();
                context.Response.OutputStream.Write(content, 0, content.Length);
            }
        }

        private static void PopulateGetRequest(MgHttpRequestParam param, HttpContext context)
        {
            foreach (var key in context.Request.QueryString.AllKeys)
            {
                param.AddParameter(key, context.Request.QueryString[key]);
            }
        }

        static void PopulatePostRequest(MgHttpRequestParam param, HttpContext context)
        {
            foreach (var key in context.Request.Form.AllKeys)
            {
                param.AddParameter(key, context.Request.Form[key]);
            }

            //NOTE: To ensure package loading operations work, set the maxRequestLength property in web.config
            //as appropriate.
            foreach (var file in context.Request.Files.AllKeys)
            {
                var postedFile = context.Request.Files[file];
                
                //We have to dump this file content to a temp location so that the mapagent handler
                //can create a file-based MgByteSource from it
                var tempPath = Path.GetTempFileName();
                postedFile.SaveAs(tempPath);

                param.AddParameter(file, tempPath);
                //tempfile is a hint to the MgHttpRequest for it to create a MgByteSource from it
                param.SetParameterType(file, "tempfile");
            }
        }

        static void HandleException(Exception ex, HttpContext context)
        {
            context.Response.StatusCode = 500;
            context.Response.Write(ex.ToString());
        }

        static void HandleUnauthorized(HttpContext context)
        {
            context.Response.StatusCode = 401;
            context.Response.AddHeader("WWW-Authenticate", "Basic realm=\"mapguide\"");
            context.Response.Write("You must enter a valid login ID and password to access this site");
        }

        static void HandleMgException(MgException ex, HttpContext context)
        {
            String msg = string.Format("{0}\n{1}", ex.GetExceptionMessage(), ex.GetStackTrace());
            if (ex is MgResourceNotFoundException || ex is MgResourceDataNotFoundException)
            {
                context.Response.StatusCode = 404;
            }
            else if (ex is MgAuthenticationFailedException || ex is MgUnauthorizedAccessException || ex is MgUserNotFoundException)
            {
                HandleUnauthorized(context);
            }
        }

        public bool IsReusable
        {
            get
            {
                return true;
            }
        }
    }
}