using System;
using System.ComponentModel;
using YukkuriMovieMaker.Plugin;

namespace BrowserMusicController
{
    public class BrowserMusicControllerViewModel : IToolViewModel
    {
#pragma warning disable CS0067
        public event EventHandler<CreateNewToolViewRequestedEventArgs>? CreateNewToolViewRequested;
        public event PropertyChangedEventHandler? PropertyChanged;
#pragma warning restore CS0067

        public string Title => "ブラウザ音楽コントローラー";

        public void LoadState(ToolState stateData)
        {
        }

        public ToolState SaveState()
        {
            return new ToolState
            {
                Title = Title,
            };
        }
    }
}
