namespace Maki.Sources.GigaViewer;

// DI needs a distinct type per registration, so each site gets a one-line sealed subclass that
// only supplies its GigaViewerSite; every behaviour lives in GigaViewerSource.

public sealed class ShonenJumpPlusSource(IHttpClientFactory httpClientFactory)
    : GigaViewerSource(httpClientFactory, GigaViewerSites.ShonenJumpPlus);

public sealed class ComicDaysSource(IHttpClientFactory httpClientFactory)
    : GigaViewerSource(httpClientFactory, GigaViewerSites.ComicDays);

public sealed class SundayWebrySource(IHttpClientFactory httpClientFactory)
    : GigaViewerSource(httpClientFactory, GigaViewerSites.SundayWebry);

public sealed class MagcomiSource(IHttpClientFactory httpClientFactory)
    : GigaViewerSource(httpClientFactory, GigaViewerSites.Magcomi);

public sealed class TonarinoYjSource(IHttpClientFactory httpClientFactory)
    : GigaViewerSource(httpClientFactory, GigaViewerSites.TonarinoYj);

public sealed class ComicZenonSource(IHttpClientFactory httpClientFactory)
    : GigaViewerSource(httpClientFactory, GigaViewerSites.ComicZenon);

public sealed class KurageBunchSource(IHttpClientFactory httpClientFactory)
    : GigaViewerSource(httpClientFactory, GigaViewerSites.KurageBunch);
