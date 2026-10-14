import { Routes } from '@angular/router';
import { InboxHomePage } from './inbox-home.page';

/** Inbox module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Inbox', component: InboxHomePage }];
