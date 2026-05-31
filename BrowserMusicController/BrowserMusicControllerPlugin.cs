using System;
using YukkuriMovieMaker.Plugin;

namespace BrowserMusicController
{
    public class BrowserMusicControllerPlugin : IToolPlugin
    {
        public Type ViewModelType => typeof(BrowserMusicControllerViewModel);

        public Type ViewType => typeof(BrowserMusicControllerPanel);

        public string Name => "ブラウザ音楽コントローラー";

        public bool AllowMultipleInstances => true;
    }
}
