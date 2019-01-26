using Newtonsoft.Json;

namespace BuildUploader.Console {

  public class SlackSettings {

    [JsonProperty("url")]
    public string Url { get; internal set; }

  }
}