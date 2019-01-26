namespace BuildUploader.Console {
  internal class BuildDefinition {

    public int BuildNumber;

    public string FileName;

    public string DownloadUrl;

    public string CommitId;

    public string CommitMessage;

    public string ScmBranch;

    public override string ToString() {
      return string.Format("Build({0})", this.FileName);
    }
  }
}
