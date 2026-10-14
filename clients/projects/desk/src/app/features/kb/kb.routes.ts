import { Routes } from '@angular/router';
import { KbHomePage } from './kb-home.page';

/** Knowledge Base module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'Knowledge Base', component: KbHomePage }];
