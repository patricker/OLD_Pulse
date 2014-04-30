using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using Pulse.Base;
using Pulse.Base.Providers;
using System.Threading;
using System.IO;
using System.Collections.Specialized;

namespace wallbase
{
    [System.ComponentModel.Description("Wallbase")]
    [ProviderConfigurationUserControl(typeof(WallbaseProviderPrefs))]
    [ProviderConfigurationClass(typeof(WallbaseImageSearchSettings))]
    [ProviderIcon(typeof(Properties.Resources), "wallbasefav")]
    public class Provider : IInputProvider
    {
        private int resultCount;
        private CookieContainer _cookies = new CookieContainer();

        public void Initialize(object args)
        {
            System.Net.ServicePointManager.Expect100Continue = false;
        }

        public void Activate(object args) { }
        public void Deactivate(object args) { }

        public PictureList GetPictures(PictureSearch ps)
        {
            WallbaseImageSearchSettings wiss = string.IsNullOrEmpty(ps.SearchProvider.ProviderConfig) ? new WallbaseImageSearchSettings() : WallbaseImageSearchSettings.LoadFromXML(ps.SearchProvider.ProviderConfig);
                                    
            //if max picture count is 0, then no maximum, else specified max
            var maxPictureCount = wiss.GetMaxImageCount(ps.MaxPictureCount);
            int pageSize = wiss.GetPageSize();
            int pageIndex = ps.PageToRetrieve; //set page to retreive if one is specified
            var imgFoundCount = 0;

            //authenticate to wallbase
            Authenticate(wiss.Username, wiss.Password);
            
            var wallResults = new List<Picture>();

            string areaURL = wiss.BuildURL();

            do
            {
                //calculate page index.  Random does not use pages, so for random just refresh with same url
                string strPageNum = (pageIndex * pageSize).ToString();

                string pageURL = areaURL.Contains("{0}") ? string.Format(areaURL, strPageNum) : areaURL;
                //string content = HttpPost(pageURL, postParams);
                string content = string.Empty;

                using (HttpUtility.CookieAwareWebClient _client = new HttpUtility.CookieAwareWebClient(_cookies))
                {
                    try
                    {
                        //if random then don't post values
                        //if (wiss.SA == "random")
                        //{
                            content = _client.DownloadString(pageURL);
                        //}
                        //else
                        //{
                        //    byte[] reqResult = _client.UploadValues(pageURL, wiss.GetPostParams());
                        //    content = System.Text.Encoding.Default.GetString(reqResult);
                        //}
                    }
                    catch (Exception ex)
                    {
                        Log.Logger.Write(string.Format("Failed to download search results from wallbase.cc, error: {0}", ex.ToString()), Log.LoggerLevels.Warnings);
                    }
                }

                if (string.IsNullOrEmpty(content))
                    break;

                //parse html and get count
                var pics = ParsePictures(content);
                imgFoundCount = pics.Count();

                //if we have an image ban list check for them
                // doing this in the provider instead of picture manager
                // ensures that our count does not go down if we have a max
                if (ps.BannedURLs != null && ps.BannedURLs.Count > 0)
                {
                    pics = (from c in pics where !(ps.BannedURLs.Contains(c.Url)) select c).ToList();
                }

                wallResults.AddRange(pics);

                //increment page index so we can get the next set of images if they exist
                pageIndex++;
            } while (imgFoundCount > 0 && wallResults.Count < maxPictureCount && ps.PageToRetrieve == 0);

            PictureList result = FetchPictures(wallResults, ps.PreviewOnly);
            result.Pictures = result.Pictures.Take(maxPictureCount).ToList();

            return result;
        }

        private PictureList FetchPictures(List<Picture> wallResults, bool previewOnly) 
        {
            var result = new PictureList() { FetchDate = DateTime.Now };

            ManualResetEvent mreThread = new ManualResetEvent(false);

            ThreadStart threadStarter = () =>
            {
                //download in parallel
                var processCounter = 0;

                try
                {
                    while (processCounter < wallResults.Count)
                    {
                        var toProcess = wallResults.Skip(processCounter).Take(60).ToList();
                        processCounter += toProcess.Count;

                        ManualResetEvent[] manualEvents = new ManualResetEvent[toProcess.Count];

                        // Queue the work items that create and write to the files.
                        for (int i = 0; i < toProcess.Count; i++)
                        {
                            manualEvents[i] = new ManualResetEvent(false);

                            ThreadPool.QueueUserWorkItem(new WaitCallback(delegate(object state)
                            {
                                object[] states = (object[])state;

                                ManualResetEvent mre = (ManualResetEvent)states[0];
                                Picture p = (Picture)states[1];

                                try
                                {
                                    //save original URL as referrer
                                    p.Properties.Add(Picture.StandardProperties.Referrer, p.Url);
                                    p.Properties.Add(Picture.StandardProperties.BanImageKey, Path.GetFileName(p.Url));

                                    //get actual image URL if this is not a preview
                                    if(!previewOnly)
                                        p.Url = GetDirectPictureUrl(p.Url);
                                    p.Id = System.IO.Path.GetFileNameWithoutExtension(p.Url);

                                    if (!string.IsNullOrEmpty(p.Url) && !string.IsNullOrEmpty(p.Id))
                                    {
                                        result.Pictures.Add(p);
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Log.Logger.Write(string.Format("Error downloading picture object from '{0}'. Exception details: {0}", ex.ToString()), Log.LoggerLevels.Errors);
                                }
                                finally
                                {
                                    mre.Set();
                                }

                            }), new object[] { manualEvents[i], toProcess[i] });
                        }

                        //wait for all items to finish
                        //one minute timeout
                        WaitHandle.WaitAll(manualEvents, 60 * 1000);
                    }
                }
                catch(Exception ex) {
                    Log.Logger.Write(string.Format("Error during multi-threaded wallbase.cc image get.  Exception details: {0}", ex.ToString()), Log.LoggerLevels.Errors);

                }
                finally
                {
                    mreThread.Set();
                }
            };

            var thread = new Thread(threadStarter);
            thread.SetApartmentState(ApartmentState.MTA);
            thread.Start();

            mreThread.WaitOne();

            return result;
        }

        //find links to pages with wallpaper only with matching resolution
        private List<Picture> ParsePictures(string content)
        {
            var picsRegex = new Regex("<a href=\"(?<link>http://wallbase.cc/wallpaper/.*?)\".*?>.*?<img.*?data-original=\"(?<img>.*?)\".*?</a>", RegexOptions.Singleline);
            var picsMatches = picsRegex.Matches(content);

            var result = new List<Picture>();
            for (var i = 0; i < picsMatches.Count; i++)
            {
                var pic = new Picture();

                pic.Url = picsMatches[i].Groups["link"].Value;
                pic.Properties.Add(Picture.StandardProperties.Thumbnail, picsMatches[i].Groups["img"].Value);

                result.Add(pic);
            }
            resultCount = result.Count;
            return result;
        }

        private string GetDirectPictureUrl(string pageUrl)
        {
            using (HttpUtility.CookieAwareWebClient cawc = new HttpUtility.CookieAwareWebClient(_cookies))
            {

                var content = cawc.DownloadString(pageUrl);
                if (string.IsNullOrEmpty(content)) return string.Empty;

                var regex = new Regex(@"<img.*src=""(?<img>.*(wallpaper.*\.(jpg|png)))""");
                //var regex = new Regex(@"\+B\('(?<img>.*?)'\)");
                var m = regex.Match(content);
                if (m.Groups["img"].Success && !string.IsNullOrEmpty(m.Groups["img"].Value))
                {
                    return m.Groups["img"].Value;
                    //byte[] decoded = Convert.FromBase64String(m.Groups["img"].Value);
                    //string final = Encoding.Default.GetString(decoded);
                    //return final;
                }

                return string.Empty;
            }
        }
        
        private void Authenticate(string username, string password)
        {
            //if we have a username/password and we aren't already authenticated then authenticate
            if (!string.IsNullOrEmpty(username) && !string.IsNullOrEmpty(password))
            {
                using(HttpUtility.CookieAwareWebClient _client = new HttpUtility.CookieAwareWebClient(_cookies))
                {
                    //check if the user is already logged in (doh!)
                    try
                    {
                        var loginReg = @"<span class=""name"".*?" + username + "</span>";
                        string homepage = _client.DownloadString("http://wallbase.cc");
                        if (Regex.Match(homepage, loginReg, RegexOptions.IgnoreCase).Success)
                        {
                            return;
                        }
                    }
                    catch(Exception ex)
                    {
                        Log.Logger.Write(string.Format("There was an error trying to check for a pre-existing wallbase auth, ignoring it.  Exception details: {0}", ex.ToString()), Log.LoggerLevels.Errors);
                    }

                    try
                    {
                        //need to extract the cross-site request forgery token from the page
                        //<img.*src=""(?<img>.*(wallpaper.*\.(jpg|png)))""
                        var csrfRegex = new Regex(@"<input type=""hidden"" name=""csrf"" value=""(?<csrf>.*)"">");
                        var refWallbase64Regex = new Regex(@"<input type=""hidden"" name=""ref"" value=""(?<ref>.*)"">");

                        string loginPage = _client.DownloadString("http://wallbase.cc/user/login");
                        Match lpM = csrfRegex.Match(loginPage);
                        Match lpWallbaseInbase64 = refWallbase64Regex.Match(loginPage);

                        if (!lpM.Success) return;

                        var loginData = new NameValueCollection();
                        loginData.Add("csrf", lpM.Groups["csrf"].Value);
                        loginData.Add("ref", lpWallbaseInbase64.Groups["ref"].Value);

                        loginData.Add("username", username);
                        loginData.Add("password", password);

                        _client.Referrer = "http://wallbase.cc/user/login";
                        _client.Headers.Add(HttpRequestHeader.ContentType, "application/x-www-form-urlencoded");

                        byte[] result = _client.UploadValues(@"http://wallbase.cc/user/do_login", "POST", loginData);

                        //we do not need the response, all we need are the cookies
                        string response = System.Text.Encoding.UTF8.GetString(result);
                    }
                    catch (Exception ex)
                    {
                        throw new WallbaseAccessDeniedException("Wallbase authentication failed. Please verify your username and password.", ex);
                    }
                }
            }
        }
    }
}
