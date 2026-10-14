import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { RouteReuseStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { IonicRouteStrategy, provideIonicAngular } from '@ionic/angular/standalone';
import { SD_AUTH_SESSION, SD_REFRESH_TOKEN_STORE, authInterceptor, provideSdCore } from '@sd/core';
import { environment } from '../environments/environment';
import { routes } from './app.routes';
import { MobileRefreshTokenStore, MobileSession } from './core/mobile-session';
import { connectivityInterceptor } from './core/network.service';
import { PushService, WebPushService } from './core/push.service';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    { provide: RouteReuseStrategy, useClass: IonicRouteStrategy },
    // useSetInputAPI: modal componentProps are applied with ComponentRef.setInput (signal inputs).
    provideIonicAngular({ useSetInputAPI: true }),
    provideRouter(routes, withComponentInputBinding()),
    // connectivity first: it sees the final outcome (after a token refresh + retry) of every API call.
    provideHttpClient(withInterceptors([connectivityInterceptor, authInterceptor])),
    provideSdCore({
      apiBaseUrl: `${environment.apiOrigin}/api/v1`,
      hubUrl: `${environment.apiOrigin}/hubs/desk`,
      portalHubUrl: `${environment.apiOrigin}/hubs/portal`,
      client: 'mobile',
      loginPath: '/login',
    }),
    { provide: SD_REFRESH_TOKEN_STORE, useExisting: MobileRefreshTokenStore },
    { provide: SD_AUTH_SESSION, useExisting: MobileSession },
    // Native builds swap this for a @capacitor/push-notifications implementation (docs/MOBILE.md → Push notifications).
    { provide: PushService, useClass: WebPushService },
  ],
};
