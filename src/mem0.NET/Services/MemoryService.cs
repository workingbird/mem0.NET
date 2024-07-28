﻿using System.Text.Encodings.Web;
using System.Text.Json;
using mem0.Core;
using mem0.Core.Model;
using mem0.Core.VectorStores;
using mem0.NET.Functions;
using mem0.NET.Options;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.ChatCompletion;
using Microsoft.SemanticKernel.Connectors.OpenAI;
using Microsoft.SemanticKernel.Embeddings;

#pragma warning disable SKEXP0001

namespace mem0.NET.Services;

public class MemoryService
{
    private const string MemoryDeductionPrompt = """
                                                 Deduce the facts, preferences, and memories from the provided text.
                                                 Just return the facts, preferences, and memories in bullet points:
                                                 Natural language text: {user_input}
                                                 User/Agent details: {metadata}

                                                 Constraint for deducing facts, preferences, and memories:
                                                 - The facts, preferences, and memories should be concise and informative.
                                                 - Don't start by "The person likes Pizza". Instead, start with "Likes Pizza".
                                                 - Don't remember the user/agent details provided. Only remember the facts, preferences, and memories.

                                                 Deduced facts, preferences, and memories:
                                                 """;

    private const string UpdateMemoryPrompt = """
                                              You are an expert at merging, updating, and organizing memories. When provided with existing memories and new information, your task is to merge and update the memory list to reflect the most accurate and current information. You are also provided with the matching score for each existing memory to the new information. Make sure to leverage this information to make informed decisions about which memories to update or merge.

                                              Guidelines:
                                              - Eliminate duplicate memories and merge related memories to ensure a concise and updated list.
                                              - If a memory is directly contradicted by new information, critically evaluate both pieces of information:
                                                  - If the new memory provides a more recent or accurate update, replace the old memory with new one.
                                                  - If the new memory seems inaccurate or less detailed, retain the original and discard the old one.
                                              - Maintain a consistent and clear style throughout all memories, ensuring each entry is concise yet informative.
                                              - If the new memory is a variation or extension of an existing memory, update the existing memory to reflect the new information.

                                              Here are the details of the task:
                                              - Existing Memories:
                                              {existing_memories}

                                              - New Memory: {memory}
                                              """;


    public async Task CreateMemoryAsync(CreateMemoryInput input,
        ITextEmbeddingGenerationService textEmbeddingGenerationService,
        IChatCompletionService chatCompletionService,
        Mem0DotNetOptions mem0DotNetOptions,
        IVectorStoreService vectorStoreService, Kernel kernel)
    {
        var embeddings = await textEmbeddingGenerationService.GenerateEmbeddingAsync(input.Data);

        var filters = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(input.UserId))
        {
            filters.Add("user_id", input.UserId);
        }

        if (!string.IsNullOrEmpty(input.AgentId))
        {
            filters.Add("agent_id", input.AgentId);
        }

        if (!string.IsNullOrEmpty(input.RunId))
        {
            filters.Add("run_id", input.RunId);
        }

        var chatHistory = new ChatHistory();

        if (string.IsNullOrEmpty(input.Prompt))
        {
            input.Prompt = MemoryDeductionPrompt.Replace("{user_input}", input.Data)
                .Replace("{metadata}", JsonSerializer.Serialize(input.MetaData));
        }

        var extracted_memories = await chatCompletionService.GetChatMessageContentAsync(new ChatHistory()
        {
            new(AuthorRole.System,
                "You are an expert at deducing facts, preferences and memories from unstructured text."),
            new(AuthorRole.User, input.Prompt)
        });

        await vectorStoreService.CreateCol(mem0DotNetOptions.CollectionName, 1536);

        var existing_memories = (
                await vectorStoreService.Search(mem0DotNetOptions.CollectionName, embeddings.ToArray(), 5, filters))
            .Select(x => new
            {
                id = x.Id,
                score = x.Score,
                metaData = x.Payload,
                text = x.Payload["data"]
            });

        var serialized_existing_memories = existing_memories.Select(x => new
        {
            memoryId = x.id,
            score = x.score,
            text = x.text
        });

        var messages = get_update_memory_messages(serialized_existing_memories, extracted_memories.Content);

        chatHistory.AddRange(messages);

        var content = await chatCompletionService.GetChatMessageContentAsync(chatHistory,
            new OpenAIPromptExecutionSettings()
            {
                ToolCallBehavior = ToolCallBehavior.AutoInvokeKernelFunctions,
            }, kernel);

        Console.WriteLine(content.Content);
    }

    private List<ChatMessageContent> get_update_memory_messages(object serializedExistingMemories,
        string existingMemories)
    {
        return new List<ChatMessageContent>()
        {
            new(AuthorRole.User,
                UpdateMemoryPrompt.Replace("{existing_memories}",
                        JsonSerializer.Serialize(serializedExistingMemories, new JsonSerializerOptions()
                        {
                            // utf8 encoding
                            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                        }))
                    .Replace("{memory}", existingMemories))
        };
    }

    public async Task CreateMemoryToolAsync(CreateMemoryToolInput input,
        ITextEmbeddingGenerationService textEmbeddingGenerationService,
        Mem0DotNetOptions mem0DotNetOptions, IVectorStoreService vectorStoreService)
    {
        var embeddings = await textEmbeddingGenerationService.GenerateEmbeddingAsync(input.Data);

        var memoryId = Guid.NewGuid().ToString();
        input.MetaData.Add("data", input.Data);
        input.MetaData.Add("created_at", DateTime.Now);

        await vectorStoreService.Insert(mem0DotNetOptions.CollectionName, [[..embeddings.ToArray()]],
            [input.MetaData],
            [memoryId]);
    }

    public async Task<VectorData> GetMemory(string memoryId, IVectorStoreService vectorStoreService,
        Mem0DotNetOptions options)
    {
        var memory = await vectorStoreService.Get(options.CollectionName, memoryId);

        if (memory == null)
        {
            return new VectorData();
        }

        return memory;
    }

    public async Task<List<VectorData>> GetMemoryAll(IVectorStoreService vectorStoreService, Mem0DotNetOptions options,
        string? userId,
        string? agentId, string? runId, uint limit = 100)
    {
        var filters = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(userId))
        {
            filters.Add("user_id", userId);
        }

        if (!string.IsNullOrEmpty(agentId))
        {
            filters.Add("agent_id", agentId);
        }

        if (!string.IsNullOrEmpty(runId))
        {
            filters.Add("run_id", runId);
        }

        var memories = await vectorStoreService.List(options.CollectionName, filters, limit);

        return memories;
    }

    public async Task<List<VectorData>> SearchMemory(IVectorStoreService vectorStoreService,
        ITextEmbeddingGenerationService textEmbeddingGenerationService, Mem0DotNetOptions options,
        string query, string? userId,
        string? agentId, string? runId, uint limit = 100)
    {
        var embeddings = await textEmbeddingGenerationService.GenerateEmbeddingAsync(query);

        var filters = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(userId))
        {
            filters.Add("user_id", userId);
        }

        if (!string.IsNullOrEmpty(agentId))
        {
            filters.Add("agent_id", agentId);
        }

        if (!string.IsNullOrEmpty(runId))
        {
            filters.Add("run_id", runId);
        }

        var memories = await vectorStoreService.Search(options.CollectionName, embeddings.ToArray(), limit, filters);

        return memories.Select(x => new VectorData()
        {
            Id = x.Id,
            Score = x.Score,
            MetaData = x.Payload
        }).ToList();
    }

    public async Task Update(UpdateMemoryInput input, MemoryTool memoryTool)
    {
        await memoryTool.UpdateMemory(input.MemoryId, input.Data);
    }

    public async Task Delete(string memoryId, MemoryTool memoryTool)
    {
        await memoryTool.DeleteMemory(memoryId);
    }

    public async Task DeleteAll(string? userId, string? agentId, string? runId, IVectorStoreService vectorStoreService,
        Mem0DotNetOptions options, MemoryTool memoryTool)
    {
        var filters = new Dictionary<string, object>();
        if (!string.IsNullOrEmpty(userId))
        {
            filters.Add("user_id", userId);
        }

        if (!string.IsNullOrEmpty(agentId))
        {
            filters.Add("agent_id", agentId);
        }

        if (!string.IsNullOrEmpty(runId))
        {
            filters.Add("run_id", runId);
        }

        if (filters.Count == 0)
        {
            throw new Exception(
                "At least one filter is required to delete all memories. If you want to delete all memories, use the `reset()` method.");
        }

        var memories = await vectorStoreService.List(options.CollectionName, filters);

        foreach (var memory in memories)
        {
            await memoryTool.DeleteMemory(memory.Id.ToString());
        }
    }

    public async Task<List<History>> GetHistory(string memoryId, IHistoryService historyService)
    {
        var histories = await historyService.GetHistories(memoryId);

        return histories;
    }

    public async Task Reset(IVectorStoreService vectorStoreService, Mem0DotNetOptions mem0DotNetOptions,
        IHistoryService historyService)
    {
        await vectorStoreService.DeleteCol(mem0DotNetOptions.CollectionName);

        await historyService.ResetHistory();
    }
}