// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.
using AdaptiveExpressions;
using AdaptiveExpressions.Memory;
using AdaptiveExpressions.Properties;
using Antlr4.Runtime.Misc;
using Antlr4.Runtime.Tree;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Text;

namespace AdaptiveCards.Templating
{
    /// <summary>
    /// an intance of this class is used in visiting a parse tree that's been generated by antlr4 parser
    /// </summary>
    public sealed class AdaptiveCardsTemplateVisitor : AdaptiveCardsTemplateParserBaseVisitor<AdaptiveCardsTemplateResult>
    {
        private Stack<DataContext> dataContext = new Stack<DataContext>();
        private readonly JToken root;
        private readonly Options options;

        /// <summary>
        /// maintains data context
        /// </summary>
        private sealed class DataContext
        {
            public JToken token;
            public SimpleObjectMemory AELMemory;
            public bool IsArrayType = false;

            public JToken RootDataContext;
            public const string rootKeyword = "$root";
            public const string dataKeyword = "$data";
            public const string indexKeyword = "$index";

            /// <summary>
            /// constructs a data context of which current data is jtoken
            /// </summary>
            /// <param name="jtoken">new data to kept as data context</param>
            /// <param name="rootDataContext">root data context</param>
            public DataContext(JToken jtoken, JToken rootDataContext)
            {
                AELMemory = (jtoken is JObject) ? new SimpleObjectMemory(jtoken) : new SimpleObjectMemory(new JObject());

                token = jtoken;
                RootDataContext = rootDataContext;

                if (jtoken is JArray)
                {
                    IsArrayType = true;
                }

                AELMemory.SetValue(dataKeyword, token);
                AELMemory.SetValue(rootKeyword, rootDataContext);
            }

            /// <summary>
            /// overload contructor that takes <paramref name="text"/> which is <c>string</c>
            /// </summary>
            /// <exception cref="JsonException"><c>JToken.Parse(text)</c> can throw JsonException if <paramref name="text"/> is invalid json</exception>
            /// <param name="text">json in string</param>
            /// <param name="rootDataContext">a root data context</param>
            public DataContext(string text, JToken rootDataContext) : this(JToken.Parse(text), rootDataContext)
            {
            }

            /// <summary>
            /// retrieve a <see cref="JObject"/> from this DataContext instance if <see cref="JToken"/> is a <see cref="JArray"/> at <paramref name="index"/>
            /// </summary>
            /// <param name="index"></param>
            /// <returns><see cref="JObject"/> at<paramref name="index"/> of a <see cref="JArray"/></returns>
            public DataContext GetDataContextAtIndex(int index)
            {
                var jarray = token as JArray;
                var jtokenAtIndex = jarray[index];
                var dataContext = new DataContext(jtokenAtIndex, RootDataContext);
                dataContext.AELMemory.SetValue(indexKeyword, index);
                return dataContext;
            }
        }

        /// <summary>
        /// a constructor for AdaptiveCardsTemplateVisitor
        /// </summary>
        /// <param name="nullSubstitutionOption">it will called upon when AEL finds no suitable functions registered in given AEL expression during evaluation the expression</param>
        /// <param name="data">json data in string which will be set as a root data context</param>
        public AdaptiveCardsTemplateVisitor(Func<string, object> nullSubstitutionOption, string data = null)
        {
            if (data?.Length != 0)
            {
                // set data as root data context
                root = JToken.Parse(data);
                PushDataContext(data, root);
            }

            // if null, set default option
            options = new Options
            {
                NullSubstitution = nullSubstitutionOption != null? nullSubstitutionOption : (path) => $"${{{path}}}"
            };
        }

        /// <summary>
        /// returns current data context
        /// </summary>
        /// <returns><see cref="DataContext"/></returns>
        private DataContext GetCurrentDataContext()
        {
            return dataContext.Count == 0 ? null : dataContext.Peek();
        }

        /// <summary>
        /// creates <see cref="JToken"/> object based on stringToParse, and pushes the object onto a stack
        /// </summary>
        /// <param name="stringToParse"></param>
        /// <param name="rootDataContext">current root data context</param>
        private void PushDataContext(string stringToParse, JToken rootDataContext)
        {
            dataContext.Push(new DataContext(stringToParse, rootDataContext));
        }

        private void PushDataContext(DataContext context)
        {
            dataContext.Push(context);
        }

        /// <summary>
        /// Given a <paramref name="jpath"/>, create a new <see cref="DataContext"/> based on a current <see cref="DataContext"/>
        /// </summary>
        /// <param name="jpath">a json selection path</param>
        private void PushTemplatedDataContext(string jpath)
        {
            DataContext parentDataContext = GetCurrentDataContext();
            if (jpath == null || parentDataContext == null)
            {
                return;
            }

            try
            {
                var (value, error) = new ValueExpression(jpath).TryGetValue(parentDataContext.AELMemory);
                if (error == null)
                {
                    dataContext.Push(new DataContext(value as string, parentDataContext.RootDataContext));
                }
            }
            catch (JsonException)
            {
                // we swallow the error here
                // as result, no data context will be set, and won't be evaluated using the given data context
            }
        }

        private void PopDataContext()
        {
            dataContext.Pop();
        }

        private bool HasDataContext()
        {
            return dataContext.Count != 0;
        }

        /// <summary>
        /// antlr runtime wil call this method when parse tree's context is <see cref="AdaptiveCardsTemplateParser.TemplateDataContext"/>
        /// <para>It is used in parsing a pair that has $data as key</para>
        /// <para>It creates new data context, and set it as current memory scope</para>
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override AdaptiveCardsTemplateResult VisitTemplateData([NotNull] AdaptiveCardsTemplateParser.TemplateDataContext context)
        {
            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (!ValidateParserRuleContext(context))
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            // get value node from pair node
            // i.e. $data : "value"
            IParseTree templateDataValueNode = context.value();
            // value was json object or json array, we take this json value and create a new data context
            if (templateDataValueNode is AdaptiveCardsTemplateParser.ValueObjectContext || templateDataValueNode is AdaptiveCardsTemplateParser.ValueArrayContext)
            {
                string childJson = templateDataValueNode.GetText();
                PushDataContext(childJson, root);
            }
            // refer to label, valueTemplateStringWithRoot in AdaptiveCardsTemplateParser.g4 for the grammar this branch is checking
            else if (templateDataValueNode is AdaptiveCardsTemplateParser.ValueTemplateStringWithRootContext)
            {
                // call a visit method for further processing
                Visit(templateDataValueNode);
            }
            // refer to label, valueTemplateString in AdaptiveCardsTemplateParser.g4 for the grammar this branch is checking
            else if (templateDataValueNode is AdaptiveCardsTemplateParser.ValueTemplateStringContext)
            {
                // tempalteString() can be zero or more due to user error
                var templateStrings = (templateDataValueNode as AdaptiveCardsTemplateParser.ValueTemplateStringContext).templateString();
                if (templateStrings?.Length == 1)
                {
                    // retrieve template literal and create a data context
                    var templateLiteral = (templateStrings[0] as AdaptiveCardsTemplateParser.TemplatedStringContext).TEMPLATELITERAL();
                    PushTemplatedDataContext(templateLiteral.GetText());
                }
            }

            return new AdaptiveCardsTemplateResult();
        }

        /// <summary>
        /// Visitor method for <c>templateRoot</c> grammar in <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <param name="context"></param>
        /// <returns><see cref="AdaptiveCardsTemplateResult"/></returns>
        public override AdaptiveCardsTemplateResult VisitTemplateStringWithRoot([NotNull] AdaptiveCardsTemplateParser.TemplateStringWithRootContext context)
        {
            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (!ValidateParserRuleContext(context))
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            // retreives templateroot token from current context; please refers to templateRoot grammar in AdaptiveCardsTemplateParser.g4
            return Visit(context.TEMPLATEROOT());
        }

        /// <summary>
        /// Visitor method for <c>templateRootData</c> grammar rule in <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <param name="context"></param>
        /// <returns><see cref="AdaptiveCardsTemplateResult"/></returns>
        public override AdaptiveCardsTemplateResult VisitTemplateRootData([NotNull] AdaptiveCardsTemplateParser.TemplateRootDataContext context)
        {
            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (!ValidateParserRuleContext(context))
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            // retrieves templateRoot of the grammar as in this method's summary
            var child = context.templateRoot();
            PushTemplatedDataContext(child.GetText());
            return new AdaptiveCardsTemplateResult();
        }

        /// <summary>
        /// Visitor method for <c>valueTemplateExpresssion</c> grammar rule <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <remarks>parsed string has a form of "$when" : ${}</remarks>
        /// <param name="context"></param>
        /// <returns>AdaptiveCardsTemplateResult</returns>
        public override AdaptiveCardsTemplateResult VisitValueTemplateExpression([NotNull] AdaptiveCardsTemplateParser.ValueTemplateExpressionContext context)
        {
            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (!ValidateParserRuleContext(context))
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            // retreives TEMPLATELITERAL token and capture its content as AdaptiveCardsTemplateResult
            AdaptiveCardsTemplateResult result = new AdaptiveCardsTemplateResult(context.GetText(), context.TEMPLATELITERAL().GetText());

            DataContext dataContext = GetCurrentDataContext();

            // if current data context is array type, we can't evalute here, so we return the captured template expression
            if (dataContext == null || dataContext.IsArrayType)
            {
                return result;
            }

            // evaluate $when
            result.WhenEvaluationResult = IsTrue(result.Predicate, dataContext.token) ?
                AdaptiveCardsTemplateResult.EvaluationResult.EvaluatedToTrue :
                AdaptiveCardsTemplateResult.EvaluationResult.EvaluatedToFalse;

            return result;
        }

        /// <summary>
        /// Visitor method for <c>valueTemplateString</c> grammar rule <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <param name="context"></param>
        /// <returns><c>AdaptiveCardsTemplateResult</c></returns>
        public override AdaptiveCardsTemplateResult VisitValueTemplateString([NotNull] AdaptiveCardsTemplateParser.ValueTemplateStringContext context)
        {
            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (!ValidateParserRuleContext(context))
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            AdaptiveCardsTemplateResult result = new AdaptiveCardsTemplateResult();
            var templateStrings = context.templateString();
            if (templateStrings.Length == 1)
            {
                var templatedStringContext = templateStrings.GetValue(0) as AdaptiveCardsTemplateParser.TemplatedStringContext;
                // strictly, this check is not needed since the only children the context can have is this type
                if (templatedStringContext != null)
                {
                    ITerminalNode[] stringChildren = templatedStringContext.STRING();
                    // if ther are no string tokens, we do not quates
                    if (stringChildren.Length == 0)
                    {
                        result.Append(ExpandTemplatedString(templatedStringContext.TEMPLATELITERAL(), true));
                        return result;
                    }
                }
            }

            result.Append(context.StringDeclOpen().GetText());

            foreach (var templateString in templateStrings)
            {
                result.Append(Visit(templateString));
            }

            result.Append(context.CLOSE().GetText());

            return result;
        }

        /// <summary>
        /// Visitor method for <c>valueObject</c> grammar rule <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <param name="context"></param>
        /// <returns><c>AdaptiveCardsTemplateResult</c></returns>
        public override AdaptiveCardsTemplateResult VisitValueObject([NotNull] AdaptiveCardsTemplateParser.ValueObjectContext context)
        {
            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (!ValidateParserRuleContext(context))
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            return VisitObj(context.obj());
        }

        /// <summary>
        /// Visitor method for <c>obj</c> grammar rule <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <param name="context"></param>
        /// <returns><c>AdaptiveCardsTemplateResult</c></returns>
        public override AdaptiveCardsTemplateResult VisitObj([NotNull] AdaptiveCardsTemplateParser.ObjContext context)
        {
            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (!ValidateParserRuleContext(context))
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            var hasDataContext = false;
            var isArrayType = false;
            var pairs = context.pair();

            // pair that was used for data context
            AdaptiveCardsTemplateParser.PairContext dataPair = null;
            // find and set data context
            // visit the first data context available, the rest is ignored
            foreach (var pair in pairs)
            {
                if (pair is AdaptiveCardsTemplateParser.TemplateDataContext || pair is AdaptiveCardsTemplateParser.TemplateRootDataContext)
                {
                    if (pair.exception == null)
                    {
                        Visit(pair);
                        hasDataContext = true;
                        isArrayType = GetCurrentDataContext().IsArrayType;
                        dataPair = pair;
                        break;
                    }
                }
            }

            int repeatsCounts = 1;
            var dataContext = GetCurrentDataContext();

            if (isArrayType && hasDataContext)
            {
                var jarray = dataContext.token as JArray;
                repeatsCounts = jarray.Count;
            }

            AdaptiveCardsTemplateResult combinedResult = new AdaptiveCardsTemplateResult();
            // indicates the number of removed json object(s)
            int removedCounts = 0;
            var comma = context.COMMA();
            string jsonPairDelimieter = (comma != null && comma.Length > 0) ? comma[0].GetText() : "";

            // loop for repeating obj parsed in the inner loop
            for (int iObj = 0; iObj < repeatsCounts; iObj++)
            {
                if (isArrayType)
                {
                    // set new data context
                    PushDataContext(dataContext.GetDataContextAtIndex(iObj));
                }

                // parse obj
                AdaptiveCardsTemplateResult result = new AdaptiveCardsTemplateResult(context.LCB().GetText());
                var whenEvaluationResult = AdaptiveCardsTemplateResult.EvaluationResult.NotEvaluated;

                for (int iPair = 0; iPair < pairs.Length; iPair++)
                {
                    var pair = pairs[iPair];
                    // if the pair refers to same pair that was used for data cotext, do not add its entry
                    if (pair != dataPair)
                    {
                        var returnedResult = Visit(pair);
                        if (returnedResult.IsWhen)
                        {
                            whenEvaluationResult = returnedResult.WhenEvaluationResult;
                        }
                        result.Append(returnedResult);

                        // add a delimiter, ','
                        if (iPair != pairs.Length - 1 && !returnedResult.IsWhen)
                        {
                            result.Append(jsonPairDelimieter);
                        }
                    }
                }

                result.Append(context.RCB().GetText());

                if (whenEvaluationResult != AdaptiveCardsTemplateResult.EvaluationResult.EvaluatedToFalse)
                {
                    if (iObj != repeatsCounts - 1)
                    {
                        result.Append(jsonPairDelimieter);
                    }
                    combinedResult.Append(result);
                }
                else
                {
                    removedCounts++;
                }

                if (isArrayType)
                {
                    PopDataContext();
                }
            }

            if (hasDataContext)
            {
                PopDataContext();
            }

            // all existing json obj in input and repeated json obj if any have been removed 
            if (removedCounts == repeatsCounts)
            {
                combinedResult.HasItBeenDropped = true;
            }

            return combinedResult;
        }

        /// <summary>
        /// Visitor method for <c>ITernminalNode</c> 
        /// <para>collects token as string and expand template if needed</para>
        /// </summary>
        /// <param name="node"></param>
        /// <returns><c>AdaptiveCardsTemplateResult</c></returns>
        public override AdaptiveCardsTemplateResult VisitTerminal(ITerminalNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (node.Symbol.Type == AdaptiveCardsTemplateLexer.TEMPLATELITERAL || node.Symbol.Type == AdaptiveCardsTemplateLexer.TEMPLATEROOT)
            {
                return ExpandTemplatedString(node);
            }

            return new AdaptiveCardsTemplateResult(node.GetText());
        }

        /// <summary>
        /// Visitor method for <c>templatdString</c> label in <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <param name="node"></param>
        /// <param name="isExpanded"></param>
        /// <returns><c>AdaptiveCardsTemplateResult</c></returns>
        public AdaptiveCardsTemplateResult ExpandTemplatedString(ITerminalNode node, bool isExpanded = false)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            if (HasDataContext())
            {
                DataContext currentDataContext = GetCurrentDataContext();
                string templateString = node.GetText();
                return new AdaptiveCardsTemplateResult(Expand(templateString, currentDataContext.AELMemory, isExpanded));
            }

            return new AdaptiveCardsTemplateResult(node.GetText());
        }

        /// <summary>
        /// Expands template expression using Adaptive Expression Library (AEL)
        /// </summary>
        /// <param name="unboundString"></param>
        /// <param name="data"></param>
        /// <param name="isTemplatedString"></param>
        /// <returns><c>string</c></returns>
        public static string Expand(string unboundString, SimpleObjectMemory data, bool isTemplatedString = false)
        {
            if (unboundString == null)
            {
                return "";
            }

            Expression exp;
            try
            {
                exp = Expression.Parse(unboundString.Substring(2, unboundString.Length - 3));
            }
            // AEL can throw any errors, for example, System.Data.Syntax error will be thrown from AEL's ANTLR 
            // when AEL encounters unknown functions.
            // We can't possibly know all errors and we simply want to leave the expression as it is when there are any exceptions
#pragma warning disable CA1031 // Do not catch general exception types
            catch (Exception)
#pragma warning restore CA1031 // Do not catch general exception types
            {
                return unboundString;
            }

            var options = new Options
            {
                NullSubstitution = (path) => $"${{{path}}}"
            };

            StringBuilder result = new StringBuilder();
            var (value, error) = exp.TryEvaluate(data, options);
            if (error == null)
            {
                if (value is string && isTemplatedString)
                {
                    result.Append("\"");
                }

                result.Append(value.ToString());

                if (value is string && isTemplatedString)
                {
                    result.Append("\"");
                }
            }
            else
            {
                result.Append("${" + unboundString + "}");
            }

            return result.ToString();
        }

        /// <summary>
        /// return the parsed result of $when from pair context
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public override AdaptiveCardsTemplateResult VisitTemplateWhen([NotNull] AdaptiveCardsTemplateParser.TemplateWhenContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }
            // when this node is visited, the children of this node is shown as below: 
            // this node is visited only when parsing was correctly done
            // [ '{', '$when', ':', ',', 'expression'] 
            var result = Visit(context.templateExpression());
            return result;
        }

        /// <summary>
        /// Visit method for <c>array</c> grammar in <c>AdaptiveCardsTemplateParser.g4</c>
        /// </summary>
        /// <param name="context"></param>
        /// <returns>AdaptiveCardsTemplateResult</returns>
        public override AdaptiveCardsTemplateResult VisitArray([NotNull] AdaptiveCardsTemplateParser.ArrayContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            if (context.exception != null)
            {
                return new AdaptiveCardsTemplateResult(context.GetText());
            }

            AdaptiveCardsTemplateResult result = new AdaptiveCardsTemplateResult(context.LSB().GetText());
            var values = context.value();
            var arrayDelimiters = context.COMMA();

            // visit each json value in json array and integrate parsed result
            for (int i = 0; i < values.Length; i++)
            {
                var value = context.value(i);
                var parsedResult = Visit(value);
                result.Append(parsedResult);
                // only add delimiter when parsedResult has not been dropped, and delimiter is needed
                if (!parsedResult.HasItBeenDropped && i != values.Length - 1 && arrayDelimiters.Length > 0)
                {
                    result.Append(arrayDelimiters[0].GetText());
                }
            }

            result.Append(context.RSB().GetText());

            return result;
        }

        /// <summary>
        /// Evaluates a predicate
        /// </summary>
        /// <param name="predicate"></param>
        /// <param name="data"></param>
        /// <returns><c>true</c> if predicate is evaluated to <c>true</c></returns>
        public static bool IsTrue(string predicate, JToken data)
        {
            var (value, error) = new ValueExpression(predicate).TryGetValue(data);
            if (error == null)
            {
                return bool.Parse(value as string);
            }
            return true;
        }

        /// <summary>
        /// Visits each children in IRuleNode
        /// </summary>
        /// <param name="node"></param>
        /// <returns></returns>
        public override AdaptiveCardsTemplateResult VisitChildren([NotNull] IRuleNode node)
        {
            if (node == null)
            {
                throw new ArgumentNullException(nameof(node));
            }

            AdaptiveCardsTemplateResult result = new AdaptiveCardsTemplateResult();

            for (int i = 0; i < node.ChildCount; i++)
            {
                result.Append(Visit(node.GetChild(i)));
            }

            return result;
        }

        private static bool ValidateParserRuleContext(Antlr4.Runtime.ParserRuleContext context)
        {
            if (context == null)
            {
                throw new ArgumentNullException(nameof(context));
            }

            // checks if parsing failed, if failed, return failed segment as string unchanged
            if (context.exception != null)
            {
                return false;
            }

            return true;
        }
    }
}
