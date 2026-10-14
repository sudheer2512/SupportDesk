import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { ApplicationConfig, provideBrowserGlobalErrorListeners, provideZonelessChangeDetection } from '@angular/core';
import { RouteReuseStrategy, provideRouter, withComponentInputBinding } from '@angular/router';
import { IonicRouteStrategy, provideIonicAngular } from '@ionic/angular/standalone';
import { SD_AUTH_SESSION, SD_REFRESH_TOKEN_STORE, authInterceptor, provideSdCore } from '@sd/core';
import { environment } from '../environments/environment';
import { routes } from './app.routes';
import { MobileRefreshTokenStore, MobileSession } from './core/mobile-session';

export const appConfig: ApplicationConfig = {
  providers: [
    provideBrowserGlobalErrorListeners(),
    provideZonelessChangeDetection(),
    { provide: RouteReuseStrategy, useClass: IonicRouteStrategy },
    // useSetInputAPI: modal componentProps are applied with ComponentRef.setInput (signal inputs).
    provideIonicAngular({ useSetInputAPI: true }),
    provideRouter(routes, withComponentInputBinding()),
    provideHttpClient(withInterceptors([authInterceptor])),
    provideSdCore({
      apiBaseUrl: `${environment.apiOrigin}/api/v1`,
      hubUrl: `${environment.apiOrigin}/hubs/desk`,
      client: 'mobile',
      loginPath: '/login',
    }),
    { provide: SD_REFRESH_TOKEN_STORE, useExisting: MobileRefreshTokenStore },
    { provide: SD_AUTH_SESSION, useExisting: MobileSession },
  ],
};
