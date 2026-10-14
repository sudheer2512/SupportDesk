import { Injectable, inject } from '@angular/core';
import { Observable } from 'rxjs';
import { ApiClient } from '../http/api-client';
import { Guid, PageResult } from '../models/common';
import {
  ConvertTopicResultDto,
  CreatePostRequest,
  ModerateRequest,
  PostDto,
  TopicDetailDto,
  TopicListItemDto,
  TopicQuery,
  UpdatePostRequest,
  UpdateTopicRequest,
} from '../models/knowledge';

const enc = encodeURIComponent;

/**
 * Community forums — agent side (`/api/v1/community…`): moderation queue, agent answers, accept answer,
 * hide/close/pin, edit, convert a topic to a ticket. Moderation needs `community.moderate`.
 */
@Injectable({ providedIn: 'root' })
export class CommunityApi {
  private readonly api = inject(ApiClient);

  topics(query: TopicQuery = {}): Observable<PageResult<TopicListItemDto>> {
    return this.api.get<PageResult<TopicListItemDto>>('community/topics', { params: query });
  }

  topic(id: Guid): Observable<TopicDetailDto> {
    return this.api.get<TopicDetailDto>(`community/topics/${enc(id)}`);
  }

  updateTopic(id: Guid, body: UpdateTopicRequest): Observable<TopicDetailDto> {
    return this.api.put<TopicDetailDto>(`community/topics/${enc(id)}`, body);
  }

  reply(id: Guid, body: CreatePostRequest): Observable<TopicDetailDto> {
    return this.api.post<TopicDetailDto>(`community/topics/${enc(id)}/posts`, body);
  }

  updatePost(postId: Guid, body: UpdatePostRequest): Observable<PostDto> {
    return this.api.put<PostDto>(`community/posts/${enc(postId)}`, body);
  }

  moderateTopic(id: Guid, body: ModerateRequest): Observable<TopicDetailDto> {
    return this.api.post<TopicDetailDto>(`community/topics/${enc(id)}/moderate`, body);
  }

  moderatePost(postId: Guid, body: ModerateRequest): Observable<PostDto> {
    return this.api.post<PostDto>(`community/posts/${enc(postId)}/moderate`, body);
  }

  accept(id: Guid, postId: Guid): Observable<TopicDetailDto> {
    return this.api.post<TopicDetailDto>(`community/topics/${enc(id)}/accept/${enc(postId)}`, {});
  }

  convertToTicket(id: Guid): Observable<ConvertTopicResultDto> {
    return this.api.post<ConvertTopicResultDto>(`community/topics/${enc(id)}/convert`, {});
  }
}
