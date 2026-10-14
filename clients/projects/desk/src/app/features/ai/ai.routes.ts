import { Routes } from '@angular/router';
import { AiHomePage } from './ai-home.page';

/** AI Assistant module routes (lazy-loaded from app.routes.ts; permission-guarded there). */
export const ROUTES: Routes = [{ path: '', title: 'AI Assistant', component: AiHomePage }];
