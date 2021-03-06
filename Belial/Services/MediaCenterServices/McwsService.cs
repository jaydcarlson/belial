﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
//using System.Net.Http;
using Belial.Models.Mcws;
using System.Xml.Serialization;
using System.IO;
using System.Xml;
using System.Net;
using Windows.UI.Xaml;
using GalaSoft.MvvmLight.Messaging;
using Belial.Messages;
using System.ComponentModel;
using Windows.UI.Xaml.Media.Imaging;
using Windows.Storage.Streams;
using Windows.Web.Http;
using Windows.Foundation;
using Windows.Web.Http.Filters;
using System.Diagnostics;
using Belial.Models.Library;

namespace Belial.Services.MediaCenterServices
{
    public class McwsService : INotifyPropertyChanged
    {
        public static McwsService Instance { get;  }
        public string ServerIp { get; internal set; }
        public string ServerPort { get; internal set; }
        public string AccessKey { get; internal set; }
        public string Password { get; internal set; }
        public string UserName { get; internal set; }

        static McwsService()
        {
            // implement singleton pattern
            Instance = Instance ?? new McwsService();
        }

        HttpClient client;

        CredentialCache cache;

        public event PropertyChangedEventHandler PropertyChanged;

        public McwsService()
        {
            cache = new CredentialCache();
            client = new HttpClient();
            //client = new TimeSpan(0, 0, 5);

            AccessKey = SettingsServices.SettingsService.Instance.ServerAccessKey;
            UserName = SettingsServices.SettingsService.Instance.ServerUserName;
            Password = SettingsServices.SettingsService.Instance.ServerPassword;

            CurrentTrack = new Track(); // gimme a blank track!
            CurrentStatus = new Status();

        }
        private Track currentTrack;
        public Track CurrentTrack { get
            {
                return currentTrack;
            } set
            {
                if (currentTrack != null && value.Key == currentTrack.Key)
                    return;
                if(currentTrack != null)
                    currentTrack.IsPlaying = false;
                currentTrack = value;
                currentTrack.IsPlaying = true;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("CurrentTrack"));
            }
        }
        public Status CurrentStatus { get; set; }

        internal async void Reconnect()
        {



            // First, fetch the IP from the AccessKey

            if (AccessKey != null && AccessKey.Length > 0)
            {
                string reqUri = string.Format("http://webplay.jriver.com/libraryserver/lookup?id={0}", AccessKey);
                var response = await (new HttpClient().GetInputStreamAsync(new Uri(reqUri)));

                var serializer = new XmlSerializer(typeof(AccessKeyLookupResponse), new XmlRootAttribute("Response"));

                AccessKeyLookupResponse record = (AccessKeyLookupResponse)serializer.Deserialize(response.AsStreamForRead());
                //client.DefaultRequestHeaders.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.ASCII.GetBytes(UserName + ":" + Password)));

                if (record.Port == null)
                {
                    return;
                }

                ServerPort = record.Port;

                var IpList = record.LocalIpList.Split(',');
                
                foreach(var ip in IpList)
                {
                    if(ip.CompareTo(record.Ip) != 0)
                    {
                        try {
                            ServerIp = ip; // Set our IP as the candidate to test
                            var result = await Get("Alive"); // this command doesn't require authentication
                            if(result["AccessKey"] == AccessKey)
                            {
                                // we found out guy
                                break;
                            } else
                            {
                                // we got a weird AccessKey back
                                ServerIp = "";
                            }

                        } catch
                        {
                            ServerIp = "";
                        }
                    }
                }

                if (ServerIp.Length == 0)
                {
                    ServerIp = record.Ip; // ugh, just use the WAN IP
                    try
                    {
                        var result = await Get("Alive");
                        if (result["AccessKey"] != AccessKey)
                        {
                            ServerIp = "";
                        }

                    }catch
                    {
                        ServerIp = "";
                    }
                }
                
                if(ServerIp.Length == 0)
                {
                    throw new Exception("Can't connect to the server");
                }

                //cache = new CredentialCache();
                //cache.Add(ServerIp, Int32.Parse(ServerPort), "Basic", new NetworkCredential(UserName, Password));

                Windows.Web.Http.Filters.HttpBaseProtocolFilter filter = new Windows.Web.Http.Filters.HttpBaseProtocolFilter();
                filter.AllowUI = false;
                if (UserName.Length > 0 && Password.Length > 0)
                {
                    filter.ServerCredential = new Windows.Security.Credentials.PasswordCredential(string.Format("http://{0}:{1}", ServerIp, ServerPort), UserName, Password);
                }
                    

                client = new HttpClient(filter);

                try
                {
                    await Get("Playback/Info?Zone=-1");
                } catch(Exception ex)
                {
                    return;
                }
                

                Task t = Task.Run(async () =>
               {
                   while(true)
                   {
                       await Windows.ApplicationModel.Core.CoreApplication.MainView.CoreWindow.Dispatcher.RunAsync(Windows.UI.Core.CoreDispatcherPriority.Normal, async () =>
                       {
                           var resp = await Get("Playback/Info?Zone=-1");

                           if(resp.ContainsKey("Status"))
                               CurrentStatus.IsPlaying = (resp["Status"] == "Playing");

                           if (resp.ContainsKey("Volume"))
                               CurrentStatus.Volume = 100.0* Double.Parse(resp["Volume"]);

                            Messenger.Default.Send<Status>(CurrentStatus);

                           int currentTrackKey = Int32.Parse(resp["FileKey"]);

                           // Check to see if we're on a new track
                           if (currentTrackKey != CurrentTrack.Key)
                           {

                               // is the current track in our library?
                               if (LibraryService.Instance.Tracks.ContainsKey(currentTrackKey))
                               {
                                   CurrentTrack = LibraryService.Instance.Tracks[currentTrackKey];
                               }
                               else
                               {
                                   // No? Alright, create a new track and populate it with the info
                                   var track = new Track();
                                   track.Key = currentTrackKey;
                                   if (resp.ContainsKey("Album"))
                                   {
                                       track.Album = LibraryService.Instance.FindOrCreateAlbum(resp["Album"]);
                                   }
                                       
                                   if (resp.ContainsKey("Artist"))
                                   {
                                       track.Artist = LibraryService.Instance.FindOrCreateArtist(resp["Artist"]);
                                   }
                                       
                                   if (resp.ContainsKey("Name"))
                                   {
                                       track.Name = resp["Name"];
                                   }

                                   track.ImageSrc = string.Format("http://{0}:{1}/{2}", ServerIp, ServerPort, resp["ImageURL"]);

                                   LibraryService.Instance.Tracks.Add(track.Key, track);

                                   CurrentTrack = track;
                               }

                               Messenger.Default.Send<Track>(CurrentTrack);

                           }

                           // Load the Now Playing list
                           var nowPlayingResponse = await GetStream("Playback/Playlist?Zone=-1&Fields=Key");
                           LibraryService.Instance.ParseNowPlayingStream(nowPlayingResponse.AsStreamForRead());


                       });
                       await Task.Delay(TimeSpan.FromSeconds(1));

                   }
               });

                // Load the library
                //IAsyncOperationWithProgress<IInputStream, HttpProgress> libraryStream = client.GetInputStreamAsync(new Uri(string.Format("http://{0}:{1}/MCWS/v1/Files/Search?Action=mpl&ActiveFile=-1&Zone=-1&ZoneType=ID&Fields=Key,Name,Artist,Album,Album%20Artist,Track%20%23,Date%20Imported,Date", ServerIp, ServerPort)));
                IAsyncOperationWithProgress<IInputStream, HttpProgress> libraryStream = GetStream("Files/Search?Action=mpl&ActiveFile=-1&Zone=-1&ZoneType=ID&Fields=Key,Name,Artist,Album,Album%20Artist,Track%20%23,Date%20Imported,Date");

                libraryStream.Completed = (info, status) =>
                {
                    LibraryService.Instance.Clear();

                    //var stream = await libraryStream;
                    //info.GetResults()
                    IInputStream stream = info.GetResults();
                    LibraryService.Instance.ParseLibraryStream(stream.AsStreamForRead());
                };

                libraryStream.Progress = (res, progress) =>
                {
                    if(progress.TotalBytesToReceive > 0)
                        Debug.WriteLine("Progress: " + 100.0 * (double)progress.BytesReceived / (double)progress.TotalBytesToReceive);
                }; 




            }
        }

        public void PlayPause()
        {
            SendAsync("Playback/PlayPause?Zone=-1&ZoneType=ID");
        }

        public void Play()
        {
            SendAsync("Playback/Pause?State=-1&Zone=0&ZoneType=ID");
        }

        public void Pause()
        {
            SendAsync("Playback/Pause?State=1&Zone=0&ZoneType=ID");
        }

        public void Stop()
        {
            SendAsync("Playback/Stop?Zone=-1&ZoneType=ID");
        }

        public void Next()
        {
            SendAsync("Playback/Next?Zone=-1&ZoneType=ID");
        }

        public void Prev()
        {
            SendAsync("Playback/Previous?Zone=-1&ZoneType=ID");
        }

        internal void SetVolume(double volume)
        {
            SendAsync(string.Format("Playback/Volume?Level={0}", volume/100.0));
        }

        public async void Play(List<Models.Library.Track> Tracks)
        {
            await SendAsync(string.Format("Playback/PlayByKey?Key={0}&Zone=-1&ZoneType=ID", Tracks[0].Key));
            for (int i = 1; i < Tracks.Count; i++)
            {
                await AddToEnd(Tracks[i]);
            }
        }

        public async void Play(Models.Library.Track Track)
        {
            await SendAsync(string.Format("Playback/PlayByKey?Key={0}&Zone=-1&ZoneType=ID", Track.Key));
        }

        // Add tracks to Now Playing
        public async void AddNext(List<Belial.Models.Library.Track> Tracks)
        {
            // We need to check for an empty Now Playing list, which we can't do yet.
            // If the list is empty, the following code will start playing the last track
            // before the other tracks are added.
            for (int i = Tracks.Count - 1; i >= 0; i--)
            {
                await AddNext(Tracks[i]);
            }

            
        }

        public async Task AddNext(Belial.Models.Library.Track Track)
        {
            await SendAsync(string.Format("Playback/PlayByKey?Key={0}&Zone=-1&ZoneType=ID&Location=Next", Track.Key));
        }

        public async void AddToEnd(List<Belial.Models.Library.Track> Tracks)
        {
            for (int i = 0; i < Tracks.Count; i++)
            {
                await AddToEnd(Tracks[i]);
            }
        }

        public async Task AddToEnd(Belial.Models.Library.Track Track)
        {
            await SendAsync(string.Format("Playback/PlayByKey?Key={0}&Zone=-1&ZoneType=ID&Location=End", Track.Key));
        }


        async Task SendAsync(string URI)
        {
            await client.GetAsync(new Uri(string.Format("http://{0}:{1}/MCWS/v1/{2}", ServerIp, ServerPort, URI)));
        }

        async Task<Dictionary<string, string>> Get(string URI)
        {
            var returnedData = await GetStream(URI);
            var dict = new Dictionary<string, string>();
            
            var serializer = new XmlSerializer(typeof(Response));
            Response data = (Response)serializer.Deserialize(returnedData.AsStreamForRead());
            if(data.Status == "OK")
            {
                foreach(var item in data.Items)
                {
                    dict.Add(item.Name, item.Value);
                }
            } else
            {
                throw new Exception("Server did not reply with OK");
            }
            return dict;
        }

        IAsyncOperationWithProgress<IInputStream, HttpProgress>  GetStream(string URI)
        {
            return client.GetInputStreamAsync(new Uri(string.Format("http://{0}:{1}/MCWS/v1/{2}", ServerIp, ServerPort, URI)));
        }
    }
}
