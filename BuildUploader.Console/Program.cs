using Newtonsoft.Json;
using System;
using System.Configuration;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Timers;

namespace BuildUploader.Console {
  class Program {
    private static System.Timers.Timer timer;
    private static string pollingFrequencyRaw;
    private static int pollingFrequency;

    static void Main(string[] args) {
      Trace.Listeners.Add(new ConsoleTraceListener());

      pollingFrequencyRaw = ConfigurationSettings.AppSettings["POLLING_FREQUENCY"];
      pollingFrequency = int.Parse(pollingFrequencyRaw) * 1000 * 60;

      ScanForNewBuilds(null, null);

      timer = new System.Timers.Timer();
      timer.Interval = pollingFrequency;
      timer.Elapsed += ScanForNewBuilds;
      timer.Start();

      System.Console.WriteLine("Press Esapce to exit, R to rescan for new builds... ");
      bool isRunning = true;
      while (isRunning) {
        var key = System.Console.ReadKey(true);
        switch (key.Key) {
          case ConsoleKey.Escape:
            isRunning = false;
            break;
          case ConsoleKey.R:
            timer.Stop();
            ScanForNewBuilds();
            timer.Start();
            break;
          default:
            System.Console.WriteLine("Press (Esc) to exit, (R) to rescan for new builds... ");
            break;
        }
      }
    }

    private static void ScanForNewBuilds(object sender, ElapsedEventArgs e) {
      ScanForNewBuilds();
    }
    private static void ScanForNewBuilds() {
      Trace.TraceInformation("Scanning for new Unity Cloud Builds at {0:MM/dd/yy H:mm}", DateTime.Now);
      System.Console.WriteLine();

      foreach (var configFile in Directory.EnumerateFiles("configs")) {
        if (!configFile.EndsWith("json")) {
          continue;
        }

        Trace.TraceInformation("Processing config file: {0}", Path.GetFileNameWithoutExtension(configFile));

        var buildConfig = JsonConvert.DeserializeObject<BuildConfiguration>(File.ReadAllText(configFile));
        var downloadBuildDataTask = Task.Run(() => DownloadUnityCloudBuildMetadata(buildConfig.UnitySettings));
        downloadBuildDataTask.Wait();
        var latestBuild = downloadBuildDataTask.Result;

        if (latestBuild != null) {
          var successfullyDownloadedBuild = DownloadUnityCloudBuild(buildConfig.SteamSettings, latestBuild);
          if (successfullyDownloadedBuild) {
            bool success = UploadBuildToSteamworks(buildConfig.SteamSettings, latestBuild);
            TryNotifySlack(buildConfig.SlackSettings, buildConfig.SteamSettings, latestBuild, success);
          }
        }

        Trace.TraceInformation("Finished processing config file: {0}", Path.GetFileNameWithoutExtension(configFile));
        System.Console.WriteLine();
      }

      Trace.TraceInformation("Finished scanning for new Unity Cloud Builds");
      Trace.TraceInformation(
          "Checking for new builds in {0} minutes at {1:MM/dd/yy H:mm}",
          pollingFrequencyRaw,
          DateTime.Now + TimeSpan.FromMilliseconds(pollingFrequency));
    }

    private static void TryNotifySlack(SlackSettings slackSettings, SteamSettings steamSettings, BuildDefinition latestBuild, bool success) {
      var slackUrl = ConfigurationSettings.AppSettings["SLACK_NOTIFICATION_URL"];
      if(slackSettings != null) {
        slackUrl = slackSettings.Url;
      }
      if (!string.IsNullOrEmpty(slackUrl)) {
        Trace.TraceInformation("Sending Slack notification");
        string payload;
        if (success) {
          payload = string.Format(
            "{0} build {1:N0} has been uploaded to the {2} branch on Steam.",
            steamSettings.DisplayName,
            latestBuild.BuildNumber,
            steamSettings.BranchName ?? "default");
        } else {
          payload = string.Format(
            "Failed to upload {0} build {1:N0} to Steam.",
            steamSettings.DisplayName,
            latestBuild.BuildNumber,
            steamSettings.BranchName ?? "default");
        }

        var message = @"{""text"": """ + payload + @"""}";

        using (var client = new HttpClient()) {
          HttpRequestMessage request = new HttpRequestMessage(HttpMethod.Post, slackUrl);
          request.Content = new StringContent(message, Encoding.UTF8, "application/json");
          var task = client.SendAsync(request);
          task.Wait();
        }
      }
    }

    private static bool UploadBuildToSteamworks(SteamSettings steamSettings, BuildDefinition buildDefinition) {
      var steamworksDir = ConfigurationSettings.AppSettings["STEAMWORKS_DIRECTORY"];

      BuildFinalAppScript(steamSettings, buildDefinition);

      Trace.TraceInformation("Invoking Steamworks SDK to upload build");
      string command = string.Format(
          @"{0}\Publish-Build.bat {1} ""{2}"" {3} {4} ""{5}""",
          steamworksDir,
          steamSettings.Username,
          steamSettings.Password,
          steamSettings.AppId,
          steamSettings.AppScript,
          Environment.CurrentDirectory + "\\" + steamSettings.ExecutablePath);

      int exitCode;
      ProcessStartInfo processInfo;
      Process process;

      processInfo = new ProcessStartInfo("cmd.exe", "/c " + command);
      processInfo.WorkingDirectory = Environment.CurrentDirectory;
      processInfo.CreateNoWindow = true;
      processInfo.UseShellExecute = false;
      // *** Redirect the output ***
      processInfo.RedirectStandardError = true;
      processInfo.RedirectStandardOutput = true;

      process = Process.Start(processInfo);
      process.WaitForExit();

      // *** Read the streams ***
      // Warning: This approach can lead to deadlocks, see Edit #2
      string output = process.StandardOutput.ReadToEnd();
      string error = process.StandardError.ReadToEnd();

      exitCode = process.ExitCode;

      Trace.TraceInformation(output);
      if (exitCode == 0) {
        Trace.TraceInformation("Steamworks SDK finished successfully");
      } else {
        Trace.TraceError(error);
        Trace.TraceError("Steamworks SDK failed");
      }

      process.Close();

      var appScriptPath = Path.Combine(steamworksDir, "scripts", steamSettings.AppScript);
      if (File.Exists(appScriptPath)) {
        Trace.TraceInformation("Removing temporary App Script file");
        File.Delete(appScriptPath);
      }

      return exitCode == 0;
    }

    private static void BuildFinalAppScript(SteamSettings steamSettings, BuildDefinition buildDefinition) {
      var steamworksDir = ConfigurationSettings.AppSettings["STEAMWORKS_DIRECTORY"];
      var appScriptTemplatePath = Path.Combine(steamworksDir, "scripts", steamSettings.AppScriptTemplate);
      if (File.Exists(appScriptTemplatePath)) {
        var allText = File.ReadAllText(appScriptTemplatePath);
        allText = allText.Replace("$buildNumber$", buildDefinition.BuildNumber.ToString());
        allText = allText.Replace("$fileName$", buildDefinition.FileName);
        allText = allText.Replace("$commitId$", buildDefinition.CommitId);
        allText = allText.Replace("$commitMessage$", buildDefinition.CommitMessage);
        allText = allText.Replace("$scmBranch$", buildDefinition.ScmBranch);

        steamSettings.AppScript = steamSettings.AppScriptTemplate.Replace("template", buildDefinition.BuildNumber.ToString());
        var appScriptPath = Path.Combine(steamworksDir, "scripts", steamSettings.AppScript);
        File.WriteAllText(appScriptPath, allText);

      } else {
        Trace.TraceError("App Script Template not found {0}", appScriptTemplatePath);
      }
    }

    private static bool DownloadUnityCloudBuild(SteamSettings steamSettings, BuildDefinition latestBuild) {
      bool success = true;
      Trace.TraceInformation("Checking whether latest build has already been processed");
      var downloadDir = ConfigurationSettings.AppSettings["DOWNLOAD_DIRECTORY"];
      var filePath = downloadDir + "/" + latestBuild.FileName;
      if (File.Exists(filePath)) {
        Trace.TraceInformation("Build already processed");
        success = false;
      } else {
        Trace.TraceInformation("Downloading new build");

        using (var webClient = new WebClient()) {
          webClient.DownloadFile(new Uri(latestBuild.DownloadUrl), filePath);
        }

        Trace.TraceInformation("Downloaded new build");

        if (Directory.Exists(steamSettings.ContentDir)) {
          Trace.TraceInformation("Deleting existing Steamworks content");
          Directory.Delete(steamSettings.ContentDir, true);
        }

        Trace.TraceInformation("Unzipping build");
        ZipFile.ExtractToDirectory(filePath, steamSettings.ContentDir);
        Trace.TraceInformation("Unzipped build");
        success = true;
      }

      return success;
    }

    public static async Task<BuildDefinition> DownloadUnityCloudBuildMetadata(UnityCloudBuildSettings cloudBuildSettings) {
      StringBuilder urlBuilder = new StringBuilder("https://build-api.cloud.unity3d.com/api/v1");
      urlBuilder.Append("/orgs/");
      urlBuilder.Append(cloudBuildSettings.OrganizationID);
      urlBuilder.Append("/projects/");
      urlBuilder.Append(cloudBuildSettings.ProjectName);
      urlBuilder.Append("/buildtargets/");
      urlBuilder.Append(cloudBuildSettings.TargetId);
      urlBuilder.Append("/builds?buildStatus=success");

      var request = new HttpRequestMessage(HttpMethod.Get, urlBuilder.ToString());
      request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
      request.Headers.Authorization = new AuthenticationHeaderValue("Basic", cloudBuildSettings.APIKey);

      var client = new HttpClient();

      Trace.TraceInformation("Downloading cloud build information.");
      BuildDefinition result;
      var response = await client.SendAsync(request);
      if (!response.IsSuccessStatusCode) {
        Trace.TraceError("Failed to download cloud build information: " + response.StatusCode);
        result = null;
      } else {
        var json = await response.Content.ReadAsStringAsync();
        Trace.TraceInformation("Parsing cloud build information.");
        dynamic successfulBuilds = JsonConvert.DeserializeObject(json);

        int latestBuildNumber = 0;
        BuildDefinition latestBuild = null;
        foreach (var build in successfulBuilds) {
          int buildNumber = build.build;
          if (latestBuild == null || latestBuildNumber < buildNumber) {
            latestBuildNumber = buildNumber;
            latestBuild = new BuildDefinition() {
              BuildNumber = build.build,
              DownloadUrl = build.links.download_primary.href,
              FileName = build.build + "_" + cloudBuildSettings.ProjectName + "_" + build.buildtargetid + "_" + build.scmBranch + '.' + build.links.download_primary.meta.type,
              CommitId = build.changeset[0].commitId,
              CommitMessage = build.changeset[0].message,
              ScmBranch = build.scmBranch,
            };
          }
        }

        Trace.TraceInformation("Found build: {0}.", latestBuildNumber);
        result = latestBuild;
      }

      return result;
    }
  }
}