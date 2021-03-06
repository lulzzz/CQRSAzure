﻿Imports CQRSAzure.EventSourcing
Imports Microsoft.WindowsAzure.Storage.Table

Namespace Azure.Table
    Public Class TableEventStreamWriter(Of TAggregate As CQRSAzure.EventSourcing.IAggregationIdentifier, TAggregateKey)
        Inherits TableEventStreamBase(Of TAggregate, TAggregateKey)
        Implements IEventStreamWriter(Of TAggregate, TAggregateKey)

#Region "Event stream information"
        Public ReadOnly Property Key As TAggregateKey Implements IEventStream(Of TAggregate, TAggregateKey).Key, IEventStreamInstanceProvider(Of TAggregate, TAggregateKey).Key
            Get
                Return m_key
            End Get
        End Property

        Private nextSequence As Long = 0
        Public ReadOnly Property RecordCount As ULong Implements IEventStream(Of TAggregate, TAggregateKey).RecordCount
            Get
                Return nextSequence
            End Get
        End Property

        Public ReadOnly Property LastAddition As Date? Implements IEventStream(Of TAggregate, TAggregateKey).LastAddition
            Get
                Throw New NotImplementedException()
            End Get
        End Property

#End Region



        Public Sub AppendEvent(EventToAppend As IEvent(Of TAggregate),
                               Optional ByVal ExpectedTopSequence As Long = 0) Implements IEventStreamWriter(Of TAggregate, TAggregateKey).AppendEvent

            'Set the next highest sequence (in case another writer has appended events)
            nextSequence = 1 + GetCurrentHighestSequence()

            'Update the current highest event number
            UpdateSequenceNumber(nextSequence)

            'Append the event
            AppendEventInternal(EventToAppend)


        End Sub



        Private Sub AppendEventInternal(EventToAppend As IEvent(Of TAggregate))

            If (MyBase.Table IsNot Nothing) Then
                'Wrap the event in its context information

                Dim commentary As String = ""
                Dim source As String = ""
                Dim who As String = ""

                If (m_context IsNot Nothing) Then
                    commentary = m_context.Commentary
                    source = m_context.Source
                    who = m_context.Who
                End If

                'make an event instance...
                Dim eventInstance = InstanceWrappedEvent(Of TAggregateKey).Wrap(m_key, EventToAppend, EventToAppend.Version)

                Dim wrappedEvent = ContextWrappedEvent(Of TAggregateKey).Wrap(eventInstance,
                                                                                nextSequence,
                                                                                commentary,
                                                                                source,
                                                                                DateTime.UtcNow,
                                                                                eventInstance.Version,
                                                                                who)

                If (wrappedEvent IsNot Nothing) Then
                    'And add that to the table
                    MyBase.Table.Execute(TableOperation.Insert(MakeDynamicTableEntity(wrappedEvent)), MyBase.RequestOptions)
                    'and increment the next sequence number
                    nextSequence += 1
                End If


            End If

        End Sub

        Public Sub AppendEvents(StartingSequence As Long, Events As IEnumerable(Of IEvent(Of TAggregate))) Implements IEventStreamWriter(Of TAggregate, TAggregateKey).AppendEvents

            'Set the next highest sequence (in case another writer has appended events)
            nextSequence = 1 + GetCurrentHighestSequence()

            If (Events IsNot Nothing) Then
                If (Events.Count > 0) Then
                    If (StartingSequence < nextSequence) Then
                        Throw New ArgumentException("Out of sequence event(s) appended")
                    Else
                        'Set the current sequence number
                        nextSequence = StartingSequence
                        UpdateSequenceNumber(nextSequence)
                        For Each evt In Events
                            AppendEventInternal(evt)
                        Next
                    End If
                End If
            End If

        End Sub

        Private Sub UpdateSequenceNumber(nextSequence As Long)

            If MyBase.AggregateKeyTable IsNot Nothing Then
                'update the sequence number
                Dim recordToSave As New DynamicTableEntity
                recordToSave.PartitionKey = AggregateClassName
                recordToSave.RowKey = m_converter.ToUniqueString(m_key)
                recordToSave.ETag = "*" 'need to set an e-tag to do a merge..maybe this should be loaded by the class itself..?
                recordToSave.Properties.Add(NameOf(TableAggregateKeyRecord.LastSequence), New EntityProperty(nextSequence))
                'merge this new record into the fray
                MyBase.AggregateKeyTable.Execute(TableOperation.Merge(recordToSave), MyBase.RequestOptions)
            End If

        End Sub

        Private m_context As IWriteContext
        Public Sub SetContext(writerContext As IWriteContext) Implements IEventStreamWriter(Of TAggregate, TAggregateKey).SetContext
            m_context = writerContext
        End Sub

        ''' <summary>
        ''' Clear out this event stream 
        ''' </summary>
        Public Sub Reset()
            If MyBase.Table IsNot Nothing Then
                'A Batch Operation allows a maximum 100 entities in the batch which must share the same PartitionKey 
                Dim projectionQuery = New TableQuery(Of DynamicTableEntity)().Where(TableQuery.GenerateFilterCondition("PartitionKey",
                    QueryComparisons.Equal, MyBase.AggregateKey)).Select({"RowKey"}).Take(100)

                Dim moreBatches As Boolean = True
                While moreBatches
                    Dim batchDelete = New TableBatchOperation()
                    For Each e In MyBase.Table.ExecuteQuery(projectionQuery)
                        batchDelete.Delete(e)
                    Next

                    moreBatches = (batchDelete.Count >= 100)

                    If (batchDelete.Count > 0) Then
                        MyBase.Table.ExecuteBatch(batchDelete)
                    End If
                End While
                'Reset the next sequence number too
                nextSequence = 0
                UpdateSequenceNumber(nextSequence)
            End If
        End Sub



        Private Sub New(ByVal AggregateDomainName As String, ByVal AggregateKey As TAggregateKey, Optional ByVal settings As ITableSettings = Nothing)
            MyBase.New(AggregateDomainName, AggregateKey, writeAccess:=True, connectionStringName:=GetWriteConnectionStringName("", settings), settings:=settings)

            'Get the current highest sequnce number (this is the only querying the writer should be allowed to do)
            nextSequence = 1 + GetCurrentHighestSequence()

        End Sub


#Region "Factory methods"

        ''' <summary>
        ''' Creates an azure blob storage based event stream reader for the given aggregate
        ''' </summary>
        ''' <param name="instance">
        ''' The instance of the aggregate for which we want to read the event stream
        ''' </param>
        ''' <returns>
        ''' </returns>
        Public Shared Function Create(ByVal instance As CQRSAzure.EventSourcing.IAggregationIdentifier(Of TAggregateKey),
                                      Optional ByVal settings As ITableSettings = Nothing) As IEventStreamWriter(Of TAggregate, TAggregateKey)

            Dim domainName As String = DomainNameAttribute.GetDomainName(instance)
            If settings IsNot Nothing Then
                If Not String.IsNullOrWhiteSpace(settings.DomainName) Then
                    domainName = settings.DomainName
                End If
            End If

            Return New TableEventStreamWriter(Of TAggregate, TAggregateKey)(domainName, instance.GetKey(), settings)

        End Function

#End Region

    End Class

    Public Module TableEventStreamWriterFactory

        ''' <summary>
        ''' Creates an azure blob storage based event stream reader for the given aggregate
        ''' </summary>
        ''' <param name="instance">
        ''' The instance of the aggregate for which we want to read the event stream
        ''' </param>
        ''' <returns>
        ''' </returns>
        Public Function Create(Of TAggregate As CQRSAzure.EventSourcing.IAggregationIdentifier,
                                   TAggregateKey)(ByVal instance As TAggregate,
                                      ByVal key As TAggregateKey,
                                      Optional ByVal settings As ITableSettings = Nothing) As IEventStreamWriter(Of TAggregate, TAggregateKey)

            Return TableEventStreamWriter(Of TAggregate, TAggregateKey).Create(instance, settings)

        End Function

        ''' <summary>
        ''' Generate a function that can be used to create an event stream writer of the given type
        ''' </summary>
        ''' <typeparam name="TAggregate">
        ''' The data type of the aggregate class
        ''' </typeparam>
        ''' <typeparam name="TAggregateKey">
        ''' The data type that provides the unique identification of an instance of the reader class
        ''' </typeparam>
        Public Function GenerateCreationFunctionDelegate(Of TAggregate As CQRSAzure.EventSourcing.IAggregationIdentifier,
                                   TAggregateKey)() As IAggregateImplementationMap.WriterCreationFunction(Of TAggregate, TAggregateKey)


            'Make delegate for this module Create() function....
            Return New IAggregateImplementationMap.WriterCreationFunction(Of TAggregate, TAggregateKey)(AddressOf Create(Of TAggregate, TAggregateKey))

        End Function

    End Module

End Namespace