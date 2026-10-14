/*
 * @sd/core — SupportDesk API client layer and app services (no UI).
 * Feature code imports from '@sd/core' only; HttpClient is used exclusively by ApiClient.
 */

// Configuration
export * from './lib/config';

// Models (mirror SupportDesk.Contracts)
export * from './lib/models/common';
export * from './lib/models/enums';
export * from './lib/models/auth';
export * from './lib/models/meta';
export * from './lib/models/tickets';
export * from './lib/models/filter';
export * from './lib/models/files';
export * from './lib/models/notifications';
export * from './lib/models/realtime';
export * from './lib/models/portal';
export * from './lib/models/communication';
export * from './lib/models/automation';
export * from './lib/models/workbench';
export * from './lib/models/approvals';
export * from './lib/models/ai';
export * from './lib/models/knowledge';
export * from './lib/models/portal-content';

// HTTP
export * from './lib/http/api-client';
export * from './lib/http/api-error';
export * from './lib/http/auth-session';
export * from './lib/http/http-context';
export * from './lib/http/interceptors';
export * from './lib/http/query-params';

// Auth
export * from './lib/auth/auth.service';
export * from './lib/auth/guards';
export * from './lib/auth/me.store';
export * from './lib/auth/permissions';

// API clients (one per module)
export * from './lib/api/auth.api';
export * from './lib/api/me.api';
export * from './lib/api/meta.api';
export * from './lib/api/tickets.api';
export * from './lib/api/views.api';
export * from './lib/api/files.api';
export * from './lib/api/notifications.api';
export * from './lib/api/channels.api';
export * from './lib/api/conversations.api';
export * from './lib/api/chat.api';
export * from './lib/api/automation.api';
export * from './lib/api/blueprints.api';
export * from './lib/api/webhooks.api';
export * from './lib/api/workbench.api';
export * from './lib/api/approvals.api';
export * from './lib/api/ai.api';
export * from './lib/api/knowledge.api';
export * from './lib/api/community.api';
export * from './lib/api/search.api';

// Realtime
export * from './lib/realtime/realtime.service';

// Customer portal
export * from './lib/portal/portal.api';
export * from './lib/portal/portal-auth.service';
export * from './lib/portal/portal-guards';
export * from './lib/portal/portal-content.api';
export * from './lib/portal/portal-tickets.api';
export * from './lib/portal/portal-realtime.service';

// App services
export * from './lib/services/clock.service';
export * from './lib/services/desktop-notifier';
export * from './lib/services/notification.store';
export * from './lib/services/shortcut-registry';
export * from './lib/services/theme.service';
export * from './lib/services/toast-bridge';

// Utilities
export * from './lib/utils/cursor-list';
export * from './lib/utils/dates';
export * from './lib/utils/fuzzy';
export * from './lib/utils/ids';
export * from './lib/utils/storage';

// Customers, products & assets, contracts and ERP integration
export * from './lib/models/customers';
export * from './lib/models/products';
export * from './lib/models/erp';
export * from './lib/api/customers.api';
export * from './lib/api/products.api';
export * from './lib/api/erp.api';
export * from './lib/utils/validation-errors';

// Mobile app: push-device registration
export * from './lib/models/mobile';
export * from './lib/api/devices.api';
// Analytics: metric catalogue, queries, reports, dashboards, exports and scheduled reports
export * from './lib/models/analytics';
export * from './lib/api/analytics.api';
// Engineering escalation (Azure DevOps / GitHub / built-in tracker) and Teams / Slack collaboration
export * from './lib/models/engineering';
export * from './lib/api/engineering.api';
