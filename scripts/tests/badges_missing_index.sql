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