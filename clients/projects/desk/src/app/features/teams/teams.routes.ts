import { Routes } from '@angular/router';
import { TeamsHomePage } from './teams-home.page';

/** Teams & Agents module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Teams & Agents', component: TeamsHomePage }];
