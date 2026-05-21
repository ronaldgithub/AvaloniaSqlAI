exec dbo.DropIndexes;

SELECT [Id]
      ,[Name]
      ,[UserId]
      ,[Date]
  FROM [dbo].[Badges]
  where 1 = (select 1) 
  and  UserId = 365789;

/*
Missing Index (Impact 99.9922): 
CREATE NONCLUSTERED INDEX [<Name of Missing Index, sysname,>] ON [dbo].[Badges] ([UserId])
*/



/* The result should look like this after AI analysis and index creation:

CREATE NONCLUSTERED INDEX [IX_Badges_UserId] ON [dbo].[Badges]
(
	[UserId] ASC
)WITH (PAD_INDEX = OFF, STATISTICS_NORECOMPUTE = OFF
, SORT_IN_TEMPDB = OFF, DROP_EXISTING = OFF, ONLINE = OFF
, ALLOW_ROW_LOCKS = ON, ALLOW_PAGE_LOCKS = ON
, FILLFACTOR = 100, OPTIMIZE_FOR_SEQUENTIAL_KEY = OFF) 
ON [PRIMARY]
GO

*/