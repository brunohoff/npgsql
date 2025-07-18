﻿using System;
using System.Collections.Generic;
using System.Data;
using System.Data.Common;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Npgsql.BackendMessages;
using Npgsql.Internal;

namespace Npgsql;

/// <inheritdoc/>
public sealed class NpgsqlBatchCommand : DbBatchCommand
{
    internal static readonly List<NpgsqlParameter> EmptyParameters = [];

    string _commandText;

    /// <inheritdoc/>
    [AllowNull]
    public override string CommandText
    {
        get => _commandText;
        set
        {
            _commandText = value ?? string.Empty;

            ResetPreparation();
            // TODO: Technically should do this also if the parameter list (or type) changes
        }
    }

    /// <inheritdoc/>
    public override CommandType CommandType { get; set; } = CommandType.Text;

    /// <inheritdoc/>
    protected override DbParameterCollection DbParameterCollection => Parameters;

    internal NpgsqlParameterCollection? _parameters;
    /// <inheritdoc cref="DbBatchCommand.Parameters"/>
    public new NpgsqlParameterCollection Parameters => _parameters ??= [];


    /// <inheritdoc/>
    public override NpgsqlParameter CreateParameter() => new();

    /// <inheritdoc/>
    public override bool CanCreateParameter => true;


    /// <summary>
    /// Appends an error barrier after this batch command. Defaults to the value of <see cref="NpgsqlBatch.EnableErrorBarriers" /> on the
    /// batch.
    /// </summary>
    /// <remarks>
    /// <para>
    ///     By default, any exception in a command causes later commands in the batch to be skipped, and earlier commands to be rolled back.
    ///     Appending an error barrier ensures that errors from this command (or previous ones) won't cause later commands to be skipped,
    ///     and that errors from later commands won't cause this command (or previous ones) to be rolled back).
    /// </para>
    /// <para>
    ///     Note that if the batch is executed within an explicit transaction, the first error places the transaction in a failed state,
    ///     causing all later commands to fail in any case. As a result, this option is useful mainly when there is no explicit transaction.
    /// </para>
    /// <para>
    ///     At the PostgreSQL wire protocol level, this corresponds to inserting a Sync message after this command, rather than grouping
    ///     all the batch's commands behind a single terminating Sync.
    /// </para>
    /// <para>
    ///     Controlling error barriers on a command-by-command basis is an advanced feature, consider enabling error barriers for the entire
    ///     batch via <see cref="NpgsqlBatch.EnableErrorBarriers" />.
    /// </para>
    /// </remarks>
    public bool? AppendErrorBarrier { get; set; }

    /// <summary>
    /// The number of rows affected or retrieved.
    /// </summary>
    /// <remarks>
    /// See the command tag in the CommandComplete message for the meaning of this value for each <see cref="StatementType"/>,
    /// https://www.postgresql.org/docs/current/static/protocol-message-formats.html
    /// </remarks>
    public ulong Rows { get; internal set; }

    /// <inheritdoc/>
    public override int RecordsAffected
    {
        get
        {
            switch (StatementType)
            {
            case StatementType.Update:
            case StatementType.Insert:
            case StatementType.Delete:
            case StatementType.Copy:
            case StatementType.Move:
            case StatementType.Merge:
                return Rows > int.MaxValue
                    ? throw new OverflowException($"The number of records affected exceeds int.MaxValue. Use {nameof(Rows)}.")
                    : (int)Rows;
            default:
                return -1;
            }
        }
    }

    /// <summary>
    /// Specifies the type of query, e.g. SELECT.
    /// </summary>
    public StatementType StatementType { get; internal set; }

    /// <summary>
    /// For an INSERT, the object ID of the inserted row if <see cref="RecordsAffected"/> is 1 and
    /// the target table has OIDs; otherwise 0.
    /// </summary>
    public uint OID { get; internal set; }

    /// <summary>
    /// The SQL as it will be sent to PostgreSQL, after any rewriting performed by Npgsql (e.g. named to positional parameter
    /// placeholders).
    /// </summary>
    public string? FinalCommandText { get; internal set; }

    /// <summary>
    /// The list of parameters, ordered positionally, as it will be sent to PostgreSQL.
    /// </summary>
    /// <remarks>
    /// If the user provided positional parameters, this references the <see cref="Parameters"/> (in batching mode) or the list
    /// backing <see cref="NpgsqlCommand.Parameters" /> (in non-batching) mode. If the user provided named parameters, this is a
    /// separate list containing the re-ordered parameters.
    /// </remarks>
    internal List<NpgsqlParameter> PositionalParameters
    {
        get => _inputParameters ??= _ownedInputParameters ??= [];
        set => _inputParameters = value;
    }

    internal bool HasParameters => _inputParameters?.Count > 0 || _ownedInputParameters?.Count > 0;

    internal List<NpgsqlParameter> CurrentParametersReadOnly => HasParameters ? PositionalParameters : EmptyParameters;

    List<NpgsqlParameter>? _ownedInputParameters;
    List<NpgsqlParameter>? _inputParameters;

    /// <summary>
    /// The RowDescription message for this query. If null, the query does not return rows (e.g. INSERT)
    /// </summary>
    internal RowDescriptionMessage? Description
    {
        get => PreparedStatement == null ? _description : PreparedStatement.Description;
        set
        {
            if (PreparedStatement == null)
                _description = value;
            else
                PreparedStatement.Description = value;
        }
    }

    RowDescriptionMessage? _description;

    /// <summary>
    /// If this statement has been automatically prepared, references the <see cref="PreparedStatement"/>.
    /// Null otherwise.
    /// </summary>
    internal PreparedStatement? PreparedStatement
    {
        get => _preparedStatement is { State: PreparedState.Unprepared }
            ? _preparedStatement = null
            : _preparedStatement;
        set => _preparedStatement = value;
    }

    PreparedStatement? _preparedStatement;

    internal NpgsqlConnector? ConnectorPreparedOn { get; set; }

    internal bool IsPreparing;

    /// <summary>
    /// Holds the server-side (prepared) ASCII statement name. Empty string for non-prepared statements.
    /// </summary>
    internal byte[] StatementName => PreparedStatement?.Name ?? [];

    /// <summary>
    /// Whether this statement has already been prepared (including automatic preparation).
    /// </summary>
    internal bool IsPrepared => PreparedStatement?.IsPrepared == true;

    /// <summary>
    /// Returns a prepared statement for this statement (including automatic preparation).
    /// </summary>
    internal bool TryGetPrepared([NotNullWhen(true)] out PreparedStatement? preparedStatement)
    {
        preparedStatement = PreparedStatement;
        return preparedStatement?.IsPrepared == true;
    }

    /// <summary>
    /// Initializes a new <see cref="NpgsqlBatchCommand"/>.
    /// </summary>
    public NpgsqlBatchCommand() : this(string.Empty) {}

    /// <summary>
    /// Initializes a new <see cref="NpgsqlBatchCommand"/>.
    /// </summary>
    /// <param name="commandText">The text of the <see cref="NpgsqlBatchCommand"/>.</param>
    public NpgsqlBatchCommand(string commandText)
        => _commandText = commandText;

    internal bool ExplicitPrepare(NpgsqlConnector connector)
    {
        if (!IsPrepared)
        {
            PreparedStatement = connector.PreparedStatementManager.GetOrAddExplicit(this);

            if (PreparedStatement?.State == PreparedState.NotPrepared)
            {
                PreparedStatement.State = PreparedState.BeingPrepared;
                IsPreparing = true;
                return true;
            }
        }

        return false;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal bool TryAutoPrepare(NpgsqlConnector connector)
    {
        // If this statement isn't prepared, see if it gets implicitly prepared.
        // Note that this may return null (not enough usages for automatic preparation).
        if (!TryGetPrepared(out var preparedStatement))
            preparedStatement = PreparedStatement = connector.PreparedStatementManager.TryGetAutoPrepared(this);
        if (preparedStatement is not null)
        {
            if (preparedStatement.State == PreparedState.NotPrepared)
            {
                preparedStatement.State = PreparedState.BeingPrepared;
                IsPreparing = true;
            }

            return true;
        }

        return false;
    }

    internal void Reset()
    {
        CommandText = string.Empty;
        StatementType = StatementType.Select;
        _description = null;
        Rows = 0;
        OID = 0;
        PreparedStatement = null;

        if (ReferenceEquals(_inputParameters, _ownedInputParameters))
            PositionalParameters.Clear();
        else if (_inputParameters is not null)
            _inputParameters = null; // We're pointing at a user's NpgsqlParameterCollection
        Debug.Assert(_inputParameters is null || _inputParameters.Count == 0);
        Debug.Assert(_ownedInputParameters is null || _ownedInputParameters.Count == 0);
    }

    internal void ApplyCommandComplete(CommandCompleteMessage msg)
    {
        StatementType = msg.StatementType;
        Rows = msg.Rows;
        OID = msg.OID;
    }

    internal void ResetPreparation() => ConnectorPreparedOn = null;

    /// <summary>
    /// Returns the <see cref="CommandText"/>.
    /// </summary>
    public override string ToString() => CommandText;
}
