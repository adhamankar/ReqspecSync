﻿using GitSync;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Quartz;
using Scheduler.Persistence;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Scheduler
{
    [DisallowConcurrentExecution(), PersistJobDataAfterExecution()]
    public class GitSyncJob : IJob
    {
        protected readonly ReqspecScheduleContext _context;
        protected readonly ILogger _logger;
        protected readonly IGitSyncProcessor _gitSyncProcessor;
        public GitSyncJob(ReqspecScheduleContext context, ILogger<GitSyncJob> logger, IGitSyncProcessor gitSyncProcessor)
        {
            _context = context;
            _logger = logger;
            _gitSyncProcessor = gitSyncProcessor;
        }
        public async Task Execute(IJobExecutionContext context)
        {
            try
            {
                _logger.LogInformation($"{DateTime.UtcNow} - Initiating GitSyncJob! ---");

                var lastSync = _context.JobSyncTrackers.Where(p => p.JobType.Code == "USERSTORYSYNC").SingleOrDefault();
                if (lastSync == null)
                {
                    var userstorySyncJobTypeId = _context.JobTypes.Where(p => p.Code == "USERSTORYSYNC").Select(p => p.Id).Single();
                    _context.JobSyncTrackers.Add(new Models.JobSyncTracker { JobTypeId = userstorySyncJobTypeId, LastModifiedOn = DateTime.UtcNow });
                }
                else
                {
                    var query = _context.UserstorySyncTrackers
                                                            .Include(p => p.UserstorySyncActionType)
                                                            .Where(p => p.CreatedOn >= lastSync.LastModifiedOn);
                    var totalUserstoriesToSync = await query.CountAsync();

                    if (totalUserstoriesToSync > 0)
                    {
                        var userstoriesToSync = await query.ToListAsync();

                        var uniqueTenantIdList = userstoriesToSync.Select(p => p.TenantId).Distinct().ToList();
                        var tenantInfo = await _context.Tenants.Where(p => uniqueTenantIdList.Contains(p.Id)).ToListAsync();

                        var groupedRecords = userstoriesToSync.GroupBy(p => p.TenantId).ToDictionary(p => p.Key, p => p.ToList());

                        tenantInfo.ForEach(p =>
                        {
                            _gitSyncProcessor.Execute(new GitSyncContext
                            {
                                AccessToken = p.AccessToken,
                                RepositoryUrl = p.RepositoryUrl,
                                Records = groupedRecords[p.Id]
                            });
                        });
                    }

                    lastSync.LastModifiedOn = DateTime.UtcNow;
                    _context.JobSyncTrackers.Update(lastSync);
                }
                await _context.SaveChangesAsync();

            }
            catch (Exception ex)
            {
                throw new JobExecutionException(ex);
            }
        }
    }
}
