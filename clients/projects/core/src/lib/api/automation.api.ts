import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import {
  AutomationCatalogDto,
  AutomationLookupsDto,
  ExecutionQuery,
  RuleConflictDto,
  RuleDefinition,
  RuleDetailDto,
  RuleExecutionDto,
  RuleSummaryDto,
  RuleVersionDto,
  SaveRuleRequest,
  SimulateRuleRequest,
  SimulationResultDto,
  ValidationResultDto,
} from '../models/automation';
import { Guid, PageResult } from '../models/common';

const id = encodeURIComponent;

/**
 * Automation rules (`/automation`): designer catalog, rule CRUD with immutable versions (draft → publish →
 * roll back), activation/order, validation, dry-run simulation, conflict detection and execution history.
 * Requires `admin.automation`.
 */
@Injectable({ providedIn: 'root' })
export class AutomationApi {
  private readonly api = inject(ApiClient);

  /** Triggers, condition fields, operators and action parameter schemas for the designer. */
  catalog(): Observable<AutomationCatalogDto> {
    return this.api.get<AutomationCatalogDto>('automation/catalog');
  }

  /** Roles and reply templates (pickers not covered by /meta/ticket-form). */
  lookups(): Observable<AutomationLookupsDto> {
    return this.api.get<AutomationLookupsDto>('automation/lookups');
  }

  list(filter: { trigger?: string | null; active?: boolean | null } = {}): Observable<RuleSummaryDto[]> {
    return this.api.get<RuleSummaryDto[]>('automation/rules', { params: filter });
  }

  get(ruleId: Guid): Observable<RuleDetailDto> {
    return this.api.get<RuleDetailDto>(`automation/rules/${id(ruleId)}`);
  }

  create(request: SaveRuleRequest): Observable<RuleDetailDto> {
    return this.api.post<RuleDetailDto>('automation/rules', request, { silent: true });
  }

  update(ruleId: Guid, request: SaveRuleRequest): Observable<RuleDetailDto> {
    return this.api.put<RuleDetailDto>(`automation/rules/${id(ruleId)}`, request, { silent: true });
  }

  delete(ruleId: Guid): Observable<void> {
    return this.api.delete<void>(`automation/rules/${id(ruleId)}`);
  }

  version(ruleId: Guid, versionId: Guid): Observable<RuleVersionDto> {
    return this.api.get<RuleVersionDto>(`automation/rules/${id(ruleId)}/versions/${id(versionId)}`);
  }

  publish(ruleId: Guid, versionId: Guid, changeNote?: string | null): Observable<RuleDetailDto> {
    return this.api.post<RuleDetailDto>(`automation/rules/${id(ruleId)}/versions/${id(versionId)}/publish`, { changeNote: changeNote ?? null }, { silent: true });
  }

  /** Republishes an older version's definition as a new version. */
  rollback(ruleId: Guid, versionId: Guid, changeNote?: string | null): Observable<RuleDetailDto> {
    return this.api.post<RuleDetailDto>(`automation/rules/${id(ruleId)}/versions/${id(versionId)}/rollback`, { changeNote: changeNote ?? null }, { silent: true });
  }

  setActive(ruleId: Guid, active: boolean): Observable<RuleDetailDto> {
    return this.api.post<RuleDetailDto>(`automation/rules/${id(ruleId)}/${active ? 'activate' : 'deactivate'}`, {}, { silent: true });
  }

  /** Sets the execution order of the given rules (in list order). */
  reorder(ruleIds: Guid[]): Observable<RuleSummaryDto[]> {
    return this.api.post<RuleSummaryDto[]>('automation/rules/reorder', { ruleIds });
  }

  rotateWebhookSecret(ruleId: Guid): Observable<RuleDetailDto> {
    return this.api.post<RuleDetailDto>(`automation/rules/${id(ruleId)}/rotate-webhook-secret`, {});
  }

  validate(trigger: string, definition: RuleDefinition): Observable<ValidationResultDto> {
    return this.api.post<ValidationResultDto>('automation/rules/validate', { trigger, definition }, { silent: true });
  }

  /** Dry run of a saved rule (or of `request.definition` when given) — no side effects. */
  simulate(ruleId: Guid | null, request: SimulateRuleRequest): Observable<SimulationResultDto> {
    const path = ruleId ? `automation/rules/${id(ruleId)}/simulate` : 'automation/rules/simulate';
    return this.api.post<SimulationResultDto>(path, request, { silent: true });
  }

  conflicts(trigger?: string | null): Observable<RuleConflictDto[]> {
    return this.api.get<RuleConflictDto[]>('automation/conflicts', { params: { trigger } });
  }

  ruleConflicts(ruleId: Guid): Observable<RuleConflictDto[]> {
    return this.api.get<RuleConflictDto[]>(`automation/rules/${id(ruleId)}/conflicts`);
  }

  executions(query: ExecutionQuery = {}): Observable<PageResult<RuleExecutionDto>> {
    return this.api.get<PageResult<RuleExecutionDto>>('automation/executions', { params: query });
  }

  execution(executionId: Guid): Observable<RuleExecutionDto> {
    return this.api.get<RuleExecutionDto>(`automation/executions/${id(executionId)}`);
  }

  retry(executionId: Guid): Observable<RuleExecutionDto> {
    return this.api.post<RuleExecutionDto>(`automation/executions/${id(executionId)}/retry`, {}, { silent: true });
  }
}
