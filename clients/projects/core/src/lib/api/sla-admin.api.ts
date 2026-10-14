import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PagedResult } from '../models/common';
import {
  BusinessCalendarDto,
  BusinessCalendarListItemDto,
  BusinessTimePreviewDto,
  BusinessTimePreviewRequest,
  CalendarHolidayDto,
  ImportHolidaysRequest,
  ImportHolidaysResultDto,
  SaveBusinessCalendarRequest,
  SaveHolidayRequest,
  SaveSlaPolicyRequest,
  SaveSlaPolicyResultDto,
  SimulateSlaRequest,
  SlaBreachItemDto,
  SlaBreachQuery,
  SlaBreachSummaryDto,
  SlaPolicyDto,
  SlaPolicyListItemDto,
  SlaSimulationDto,
} from '../models/sla-admin';

const id = (value: Guid) => encodeURIComponent(value);

/**
 * SLA administration (`/sla`): policies (targets, escalations, evaluation order, simulation against a ticket),
 * business calendars with holidays (CSV import, business-time preview) and live breach lists.
 * Editing needs admin.sla (calendars: admin.sla or admin.organisation); supervisors may read.
 */
@Injectable({ providedIn: 'root' })
export class SlaAdminApi {
  private readonly api = inject(ApiClient);

  policies(): Observable<SlaPolicyListItemDto[]> {
    return this.api.get<SlaPolicyListItemDto[]>('sla/policies');
  }

  policy(policyId: Guid): Observable<SlaPolicyDto> {
    return this.api.get<SlaPolicyDto>(`sla/policies/${id(policyId)}`);
  }

  savePolicy(policyId: Guid | null, request: SaveSlaPolicyRequest): Observable<SaveSlaPolicyResultDto> {
    return policyId
      ? this.api.put<SaveSlaPolicyResultDto>(`sla/policies/${id(policyId)}`, request, { silent: true })
      : this.api.post<SaveSlaPolicyResultDto>('sla/policies', request, { silent: true });
  }

  deletePolicy(policyId: Guid): Observable<void> {
    return this.api.delete<void>(`sla/policies/${id(policyId)}`, { silent: true });
  }

  reorder(policyIds: Guid[]): Observable<SlaPolicyListItemDto[]> {
    return this.api.put<SlaPolicyListItemDto[]>('sla/policies/order', { policyIds }, { silent: true });
  }

  simulate(request: SimulateSlaRequest): Observable<SlaSimulationDto> {
    return this.api.post<SlaSimulationDto>('sla/simulate', request, { silent: true });
  }

  calendars(): Observable<BusinessCalendarListItemDto[]> {
    return this.api.get<BusinessCalendarListItemDto[]>('sla/calendars');
  }

  calendar(calendarId: Guid): Observable<BusinessCalendarDto> {
    return this.api.get<BusinessCalendarDto>(`sla/calendars/${id(calendarId)}`);
  }

  saveCalendar(calendarId: Guid | null, request: SaveBusinessCalendarRequest): Observable<BusinessCalendarDto> {
    return calendarId
      ? this.api.put<BusinessCalendarDto>(`sla/calendars/${id(calendarId)}`, request, { silent: true })
      : this.api.post<BusinessCalendarDto>('sla/calendars', request, { silent: true });
  }

  deleteCalendar(calendarId: Guid): Observable<void> {
    return this.api.delete<void>(`sla/calendars/${id(calendarId)}`, { silent: true });
  }

  saveHoliday(calendarId: Guid, holidayId: Guid | null, request: SaveHolidayRequest): Observable<CalendarHolidayDto> {
    return holidayId
      ? this.api.put<CalendarHolidayDto>(`sla/calendars/${id(calendarId)}/holidays/${id(holidayId)}`, request, { silent: true })
      : this.api.post<CalendarHolidayDto>(`sla/calendars/${id(calendarId)}/holidays`, request, { silent: true });
  }

  deleteHoliday(calendarId: Guid, holidayId: Guid): Observable<void> {
    return this.api.delete<void>(`sla/calendars/${id(calendarId)}/holidays/${id(holidayId)}`);
  }

  importHolidays(calendarId: Guid, request: ImportHolidaysRequest): Observable<ImportHolidaysResultDto> {
    return this.api.post<ImportHolidaysResultDto>(`sla/calendars/${id(calendarId)}/holidays/import`, request, { silent: true });
  }

  preview(calendarId: Guid, request: BusinessTimePreviewRequest): Observable<BusinessTimePreviewDto> {
    return this.api.post<BusinessTimePreviewDto>(`sla/calendars/${id(calendarId)}/preview`, request, { silent: true });
  }

  breaches(query: SlaBreachQuery = {}): Observable<PagedResult<SlaBreachItemDto>> {
    return this.api.get<PagedResult<SlaBreachItemDto>>('sla/breaches', { params: query });
  }

  breachSummary(departmentId: Guid | null = null): Observable<SlaBreachSummaryDto> {
    return this.api.get<SlaBreachSummaryDto>('sla/breaches/summary', { params: { departmentId } });
  }
}
